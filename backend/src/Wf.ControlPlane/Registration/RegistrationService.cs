using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using Wf.Contracts.Manifest;
using Wf.Contracts.Registration;
using Wf.Contracts.Registry;
using Wf.ControlPlane.Registry;
using Wf.ControlPlane.Engine.Agents;
using Wf.ControlPlane.Storage;
using Wf.Validation;
using Wf.Validation.Rules;
using Wf.Validation.SemVer;

namespace Wf.ControlPlane.Registration;

public sealed class RegistrationValidationException(ImmutableArray<ValidationError> errors)
    : Exception("The registration failed validation.")
{
    public ImmutableArray<ValidationError> Errors { get; } = errors;
}

public sealed class RegistrationConflictException(string message, ImmutableArray<ValidationError> errors = default) : Exception(message)
{
    public ImmutableArray<ValidationError> Errors { get; } = errors.IsDefault ? [] : errors;
}

public sealed class RegistrationService(NpgsqlDataSource dataSource, IBlobStore blobs, RegistryWriter registryWriter,
    TimeProvider timeProvider)
{
    public const long MaximumPackageBytes = 512L * 1024 * 1024;
    private const int MaximumSourceBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions ManifestOptions = new(WorkflowManifestJsonContext.Default.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false
    };

    public async Task<RegistrationResponse> RegisterAsync(RegistrationRequest request, string actor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ValidateProvenance(request);
        ScheduleFileApplier.ValidateRequest(request);
        WorkflowManifest? manifest = null;
        BlobContent? package = null;
        byte[]? sourceZip = null;
        BlobContent? source = null;
        string[]? identity = null;
        TeamRegistry? registry = null;
        if (request.WorkflowId is { } workflowId)
        {
            identity = workflowId.Split('/');
            if (identity.Length != 3 || identity.Any(part => !ValidIdentifier(part)))
                throw Invalid("$.workflowId", "Use an exact team/type/name identity, with at most 128 Unicode characters per segment.");
        }
        if (request.Manifest is { } rawManifest)
        {
            if (identity is null) throw Invalid("$.workflowId", "A workflow identity is required with a manifest.");
            try
            {
                var errors = ManifestRules.Validate(rawManifest, cancellationToken: cancellationToken);
                if (!errors.IsEmpty) throw new RegistrationValidationException(errors);
                manifest = rawManifest.Deserialize<WorkflowManifest>(ManifestOptions);
                if (manifest is null || manifest.Metadata is null || string.IsNullOrWhiteSpace(manifest.SdkVersion) ||
                    manifest.Version is not { Length: > 0 and <= 256 } || manifest.Stages.IsDefault ||
                    manifest.Nodes.IsDefault || manifest.Variables.IsDefault || manifest.RunTimeout <= TimeSpan.Zero ||
                    manifest.Name != request.WorkflowId || manifest.Metadata.Type != identity[1])
                    throw Invalid("$.manifest", "Supply a complete manifest matching the workflow identity; SemVer is limited to 256 characters.");
                _ = ApiBaseline.FromManifest(manifest);
                var validation = Validator.Validate(manifest,
                    request.Schedules is { } scheduleText ? Encoding.UTF8.GetBytes(scheduleText) : (ReadOnlyMemory<byte>?)null,
                    request.Registry is { } companion ? Encoding.UTF8.GetBytes(companion.GetRawText()) : (ReadOnlyMemory<byte>?)null,
                    scheduleFile: request.ScheduleSourcePath ?? Wf.Validation.Schedules.ScheduleFileValidator.FileName,
                    cancellationToken: cancellationToken);
                if (!validation.IsValid) throw new RegistrationValidationException(validation.Errors);
                var policyErrors = PolicyRules.Validate(manifest.Nodes, manifest.RunTimeout)
                    .Where(diagnostic => diagnostic.Severity == PolicyDiagnosticSeverity.Error)
                    .Select(diagnostic => new ValidationError("REG001", "workflow.cs", "$.nodes",
                        "The workflow execution policy is invalid.", diagnostic.Message)).ToImmutableArray();
                if (!policyErrors.IsEmpty) throw new RegistrationValidationException(policyErrors);
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidDataException)
            {
                throw Invalid("$.manifest", "Supply a valid shared workflow manifest.");
            }
            if (request.Package is null || request.Package.Size is <= 0 or > MaximumPackageBytes)
                throw Invalid("$.package", "Provide a package digest and byte count between 1 byte and 512 MiB.");
            try { package = new(BlobKind.Packages, request.Package.Sha256, request.Package.Size); }
            catch (ArgumentException) { throw Invalid("$.package.sha256", "Provide a 64-character SHA-256 hex digest."); }
            if (request.Source is null) throw Invalid("$.source", "Workflow source is required with a manifest.");
            sourceZip = SourceArchive(request.Source);
            source = new(BlobKind.Sources, Convert.ToHexStringLower(SHA256.HashData(sourceZip)), sourceZip.Length);
        }
        else if (request.Package is not null || request.Source is not null)
            throw Invalid("$.manifest", "Package and source must accompany a manifest.");

        if (request.Registry is { } rawRegistry)
        {
            if (manifest is null)
            {
                var errors = TeamRegistryRules.Validate(Encoding.UTF8.GetBytes(rawRegistry.GetRawText()),
                    cancellationToken: cancellationToken);
                if (!errors.IsEmpty) throw new RegistrationValidationException(errors);
            }
            registry = rawRegistry.Deserialize(TeamRegistryJsonContext.Default.TeamRegistry)!;
        }
        if (manifest is null && registry is null && request.Schedules is null)
            throw Invalid("$", "Supply a workflow manifest, schedule file, or team registry.");
        var team = identity?[0] ?? registry!.Team;
        if (registry is not null && registry.Team != team)
            throw Invalid("$.registry.team", "Registry ownership must match the workflow identity.");
        if (package is not null)
        {
            try { await blobs.DownloadAsync(package, Stream.Null, cancellationToken); }
            catch (FileNotFoundException) { throw Invalid("$.package", "Upload the verified package before registration."); }
            catch (DirectoryNotFoundException) { throw Invalid("$.package", "Upload the verified package before registration."); }
            catch (Azure.RequestFailedException error) when (error.Status == 404)
            { throw Invalid("$.package", "Upload the verified package before registration."); }
            catch (InvalidDataException) { throw Invalid("$.package", "The stored package does not match its declared content."); }
            await using var input = new MemoryStream(sourceZip!, writable: false);
            await blobs.UploadAsync(source!, input, cancellationToken);
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var gate = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended('wf.registry:'||current_schema()||':'||$1,0))", connection, transaction))
        {
            gate.Parameters.AddWithValue(team);
            await gate.ExecuteNonQueryAsync(cancellationToken);
        }
        if (manifest is not null)
        {
            var selectors = await MachineSelectorInventory.ValidateAsync(connection, transaction, manifest, null, cancellationToken);
            if (!selectors.IsEmpty) throw new RegistrationValidationException(selectors);
        }
        var previousRegistryHash = registry is null ? null : await RegistryHash(connection, transaction, team, cancellationToken);
        Guid? snapshotId = null;
        if (registry is not null && await registryWriter.UpsertAsync(connection, transaction, registry, cancellationToken))
        {
            snapshotId = Guid.CreateVersion7();
            await using var snapshot = new NpgsqlCommand("""
                INSERT INTO registry_snapshots(id,team_id,git_commit,git_author,registered_by,content)
                VALUES($1,$2,$3,$4,$5,$6)
                """, connection, transaction);
            snapshot.Parameters.AddWithValue(snapshotId.Value);
            snapshot.Parameters.AddWithValue(team);
            snapshot.Parameters.AddWithValue(request.GitCommit);
            snapshot.Parameters.AddWithValue(request.GitAuthor);
            snapshot.Parameters.AddWithValue(actor);
            snapshot.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(registry, TeamRegistryJsonContext.Default.TeamRegistry));
            await snapshot.ExecuteNonQueryAsync(cancellationToken);
        }

        Guid? versionId = null;
        Guid? previousVersion = null;
        if (manifest is not null)
        {
            await using (var readRegistry = new NpgsqlCommand("SELECT registry::text FROM teams WHERE id=$1", connection, transaction))
            {
                readRegistry.Parameters.AddWithValue(team);
                var json = await readRegistry.ExecuteScalarAsync(cancellationToken) as string;
                if (json is null) throw Invalid("$.registry", "Register the owning team and type first or include their registry.");
                var errors = TeamRegistryRules.ValidateWorkflow(Encoding.UTF8.GetBytes(json), manifest.Metadata);
                if (!errors.IsEmpty) throw new RegistrationValidationException(errors);
            }
            var manifestJson = JsonSerializer.Serialize(manifest, WorkflowManifestJsonContext.Default.WorkflowManifest);
            WorkflowManifest? previous = null;
            await using (var existing = new NpgsqlCommand("""
                SELECT w.deleted_at,w.active_version_id,v.manifest::text FROM workflows w
                LEFT JOIN workflow_versions v ON v.id=w.latest_version_id WHERE w.id=$1 FOR UPDATE OF w
                """, connection, transaction))
            {
                existing.Parameters.AddWithValue(request.WorkflowId!);
                await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    if (!reader.IsDBNull(0)) throw new RegistrationConflictException("A deleted workflow cannot be registered.");
                    if (!reader.IsDBNull(1)) previousVersion = reader.GetGuid(1);
                    if (!reader.IsDBNull(2))
                        previous = JsonSerializer.Deserialize(reader.GetString(2), WorkflowManifestJsonContext.Default.WorkflowManifest)
                            ?? throw new InvalidDataException("A stored workflow manifest is null.");
                }
            }
            bool exists;
            await using (var duplicate = new NpgsqlCommand("""
                SELECT manifest::text,package_sha256=$3 AND package_size=$4 AND source_sha256=$5 AND source_size=$6
                FROM workflow_versions WHERE workflow_id=$1 AND semver_key=split_part($2,'+',1)
                """, connection, transaction))
            {
                duplicate.Parameters.AddWithValue(request.WorkflowId!);
                duplicate.Parameters.AddWithValue(manifest.Version);
                duplicate.Parameters.AddWithValue(Convert.FromHexString(package!.Sha256));
                duplicate.Parameters.AddWithValue(package.SizeBytes);
                duplicate.Parameters.AddWithValue(Convert.FromHexString(source!.Sha256));
                duplicate.Parameters.AddWithValue(source.SizeBytes);
                await using var reader = await duplicate.ExecuteReaderAsync(cancellationToken);
                exists = await reader.ReadAsync(cancellationToken);
                if (exists)
                {
                    var stored = JsonSerializer.Deserialize<WorkflowManifest>(reader.GetString(0), ManifestOptions)
                        ?? throw new InvalidDataException("A stored workflow manifest is null.");
                    if (!reader.GetBoolean(1) || !JsonElement.DeepEquals(
                            JsonSerializer.SerializeToElement(stored, WorkflowManifestJsonContext.Default.WorkflowManifest),
                            JsonSerializer.SerializeToElement(manifest, WorkflowManifestJsonContext.Default.WorkflowManifest)))
                        throw new RegistrationConflictException("This workflow version already has different immutable content.");
                }
            }
            if (!exists)
            {
                if (previous is not null)
                {
                    var comparison = BreakingChangeClassifier.Classify(ApiBaseline.FromManifest(previous), ApiBaseline.FromManifest(manifest));
                    if (!comparison.IsValid) throw new RegistrationConflictException(
                        "The workflow version does not satisfy the required semantic version bump.", comparison.Errors);
                }
                versionId = Guid.CreateVersion7();
                await using (var workflow = new NpgsqlCommand("""
                    INSERT INTO workflows(id,type_id) VALUES($1,$2) ON CONFLICT(id) DO NOTHING
                    """, connection, transaction))
                {
                    workflow.Parameters.AddWithValue(request.WorkflowId!);
                    workflow.Parameters.AddWithValue(identity![0] + "/" + identity[1]);
                    await workflow.ExecuteNonQueryAsync(cancellationToken);
                }
                await using (var version = new NpgsqlCommand("""
                    INSERT INTO workflow_versions(id,workflow_id,semver,manifest,package_uri,package_sha256,package_size,
                        source_uri,source_sha256,source_size,git_commit,git_author,registered_by,change_note,sdk_version)
                    VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15)
                    """, connection, transaction))
                {
                    version.Parameters.AddWithValue(versionId.Value);
                    version.Parameters.AddWithValue(request.WorkflowId!);
                    version.Parameters.AddWithValue(manifest.Version);
                    version.Parameters.AddWithValue(NpgsqlDbType.Jsonb, manifestJson);
                    version.Parameters.AddWithValue(package!.Key);
                    version.Parameters.AddWithValue(Convert.FromHexString(package.Sha256));
                    version.Parameters.AddWithValue(package.SizeBytes);
                    version.Parameters.AddWithValue(source!.Key);
                    version.Parameters.AddWithValue(Convert.FromHexString(source.Sha256));
                    version.Parameters.AddWithValue(source.SizeBytes);
                    version.Parameters.AddWithValue(request.GitCommit);
                    version.Parameters.AddWithValue(request.GitAuthor);
                    version.Parameters.AddWithValue(actor);
                    version.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)request.ChangeNote ?? DBNull.Value);
                    version.Parameters.AddWithValue(manifest.SdkVersion);
                    await version.ExecuteNonQueryAsync(cancellationToken);
                }
                await using var activate = new NpgsqlCommand(
                    "UPDATE workflows SET active_version_id=$2,latest_version_id=$2 WHERE id=$1", connection, transaction);
                activate.Parameters.AddWithValue(request.WorkflowId!);
                activate.Parameters.AddWithValue(versionId.Value);
                await activate.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        var schedules = request.WorkflowId is not null && (manifest is not null || request.Schedules is not null)
            ? await ScheduleFileApplier.ApplyAsync(connection, transaction, request, actor, timeProvider.GetUtcNow(), cancellationToken)
            : null;
        var response = new RegistrationResponse(versionId, snapshotId, schedules?.ChangedVersion);
        if (response.Changed)
        {
            await using (var audit = new NpgsqlCommand("""
                INSERT INTO audit_log(actor,actor_kind,action,target_kind,target_id,team_id,summary)
                VALUES($1,'App','Register',$2,$3,$4,$5)
                """, connection, transaction))
            {
                audit.Parameters.AddWithValue(actor);
                audit.Parameters.AddWithValue(request.WorkflowId is null ? "Team" : "Workflow");
                audit.Parameters.AddWithValue(request.WorkflowId ?? team);
                audit.Parameters.AddWithValue(team);
                audit.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(new
                {
                    before = new { workflowVersion = previousVersion, registrySha256 = previousRegistryHash,
                        scheduleConfigVersion = schedules?.PreviousVersion },
                    after = new
                    {
                        changes = JsonSerializer.SerializeToElement(response, RegistrationJsonContext.Default.RegistrationResponse),
                        registrySha256 = registry is null ? null : await RegistryHash(connection, transaction, team, cancellationToken)
                    }
                }));
                await audit.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var notify = new NpgsqlCommand("SELECT pg_notify('registry',$1)", connection, transaction);
            notify.Parameters.AddWithValue(request.WorkflowId ?? team);
            await notify.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    private static void ValidateProvenance(RegistrationRequest request)
    {
        if (request.GitCommit is null || request.GitCommit.Length is not (40 or 64) ||
            !request.GitCommit.All(character => char.IsAsciiHexDigitLower(character)))
            throw Invalid("$.gitCommit", "Provide a lowercase 40- or 64-character Git commit hash.");
        if (string.IsNullOrWhiteSpace(request.GitAuthor) || request.GitAuthor.Length > 512 || request.GitAuthor.Any(char.IsControl))
            throw Invalid("$.gitAuthor", "Provide a nonempty Git author of at most 512 characters without control characters.");
        if (request.ChangeNote is { Length: > 4000 })
            throw Invalid("$.changeNote", "Change notes are limited to 4000 characters.");
    }

    private static bool ValidIdentifier(string value) => !string.IsNullOrWhiteSpace(value) && value is not ("." or "..") &&
        value.EnumerateRunes().Take(129).Count() <= 128 && !value.Any(character => character is '\\' || char.IsControl(character));

    private static async Task<string?> RegistryHash(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string team, CancellationToken cancellationToken)
    {
        await using var query = new NpgsqlCommand("SELECT registry::text FROM teams WHERE id=$1", connection, transaction);
        query.Parameters.AddWithValue(team);
        var json = await query.ExecuteScalarAsync(cancellationToken) as string;
        return json is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static byte[] SourceArchive(string source)
    {
        byte[] bytes;
        try { bytes = new UTF8Encoding(false, true).GetBytes(source); }
        catch (EncoderFallbackException) { throw Invalid("$.source", "Source must be valid UTF-8 text."); }
        if (bytes.Length is 0 or > MaximumSourceBytes)
            throw Invalid("$.source", "Source must contain between 1 byte and 1 MiB of UTF-8 text.");
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("workflow.cs", CompressionLevel.NoCompression);
            entry.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            entry.ExternalAttributes = 0;
            using var stream = entry.Open();
            stream.Write(bytes);
        }
        return output.ToArray();
    }

    private static RegistrationValidationException Invalid(string path, string hint) =>
        new([new("REG001", "registration.json", path, "The registration request is invalid.", hint)]);
}
