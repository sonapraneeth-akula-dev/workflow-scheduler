using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

[assembly: InternalsVisibleTo("Wf.Cli.Tests")]

namespace Wf.Cli.Commands;

public static class LocalCommand
{
    public const string Usage =
        "Usage: wf local <up|down> [--env dev|ppe]\n" +
        "Default: ppe. Run inside a workflow-scheduler source checkout. " +
        "up publishes and builds a local Docker Compose stack; down stops only that environment, preserving data and keys.";
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileModeBits = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static Task<int> RunAsync(IReadOnlyList<string> arguments, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default) =>
        RunAsync(arguments, output, error, Directory.GetCurrentDirectory(), null, cancellationToken);

    internal static async Task<int> RunAsync(IReadOnlyList<string> arguments, TextWriter output, TextWriter error,
        string workingDirectory, Func<ProcessStartInfo, CancellationToken, Task<int>>? runner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (arguments is ["--help"] or ["-h"] or ["up", "--help"] or ["up", "-h"] or ["down", "--help"] or ["down", "-h"])
        {
            await output.WriteLineAsync(Usage);
            return 0;
        }
        var environment = "ppe";
        var stage = "argument validation";
        try
        {
            if (arguments is not ["up"] and not ["down"] and not ["up", "--env", "dev" or "ppe"] and
                not ["down", "--env", "dev" or "ppe"])
                throw new ArgumentException("Use up or down with at most one --env dev|ppe.");
            if (arguments.Count == 3) environment = arguments[2];
            cancellationToken.ThrowIfCancellationRequested();
            stage = "source checkout validation";
            var repo = FindRepository(workingDirectory);
            var appHost = Path.Combine(repo, "backend", "src", "Wf.AppHost", "Wf.AppHost.csproj");
            var composeOverride = Path.Combine(repo, "infra", "local", "compose.override.yml");
            var state = Path.Combine(repo, ".wf", "local", environment);
            var runtime = Path.Combine(state, "runtime.env");
            var publish = Path.Combine(state, "publish");
            var compose = Path.Combine(publish, "docker-compose.yaml");
            var isUp = arguments[0] == "up";
            stage = "local state validation";
            RejectLinks(state);
            if (!isUp && !Directory.Exists(state))
                throw new LocalFailure("Saved state is missing; run wf local up with the same --env first.");
            foreach (var directory in new[] { Path.Combine(repo, ".wf"), Path.Combine(repo, ".wf", "local"), state })
                PrivateDirectory(directory, create: isUp);
            stage = "environment lock";
            using var ownership = AcquireLock(Path.Combine(state, "operation.lock"));
            RejectTreeLinks(state);
            cancellationToken.ThrowIfCancellationRequested();
            stage = "runtime.env validation";
            if (!File.Exists(runtime))
            {
                if (!isUp) throw new LocalFailure("Saved runtime.env is missing; refusing to generate replacement keys during down.");
                await CreateRuntime(runtime, cancellationToken);
            }
            var secrets = await ReadRuntime(runtime, cancellationToken);
            var bind = environment == "dev" ? "127.2.6.2" : "127.2.6.7";
            var project = "kardia-" + environment;
            runner ??= (start, token) => RunProcessAsync(start, output, error, secrets.Values.ToArray(), token);

            ProcessStartInfo Start(string executable, IEnumerable<string> args)
            {
                var start = new ProcessStartInfo(executable)
                {
                    WorkingDirectory = repo, UseShellExecute = false,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                foreach (var argument in args) start.ArgumentList.Add(argument);
                start.Environment["KARDIA_ENVIRONMENT"] = environment;
                start.Environment["KARDIA_BIND_IP"] = bind;
                start.Environment["KARDIA_SUBNET"] = environment == "dev" ? "172.30.62.0/24" : "172.30.67.0/24";
                start.Environment["KARDIA_REPOSITORY_ROOT"] = repo;
                // Environment wins over Compose's env-file; never inherit conflicting host credentials.
                foreach (var (key, value) in secrets) start.Environment[key] = value;
                return start;
            }

            async Task Execute(string executable, IEnumerable<string> args)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var exit = await runner(Start(executable, args), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (exit != 0) throw new LocalFailure($"Process exited with code {exit}. Check {executable} installation and the stage diagnostics.");
            }

            if (isUp)
            {
                stage = "Aspire publish";
                PrivateDirectory(publish, create: true);
                // A successful publisher must produce fresh output, not silently reuse an old deployment.
                var previous = File.Exists(compose) ? Path.Combine(state, ".previous-compose-" + Guid.NewGuid().ToString("N")) : null;
                if (previous is not null) File.Move(compose, previous);
                var published = false;
                try
                {
                    await Execute("aspire",
                        ["publish", "--apphost", appHost, "--output-path", publish, "--environment", environment,
                         "--non-interactive", "--nologo", "--", "--Local:Compose=true", "--Local:ComposeEnvironment=" + environment]);
                    stage = "saved Compose validation";
                    RejectTreeLinks(state);
                    ValidateCompose(compose);
                    published = true;
                }
                finally
                {
                    // Failed/cancelled republish must not strand an already-running project's down command.
                    if (previous is not null)
                    {
                        RejectLinks(compose);
                        if (published) File.Delete(previous);
                        else File.Move(previous, compose, overwrite: true);
                    }
                }
            }
            stage = "saved Compose validation";
            ValidateCompose(compose);
            string[] prefix =
                ["compose", "--project-directory", repo, "--env-file", runtime, "-p", project,
                 "-f", compose, "-f", composeOverride];
            if (isUp)
            {
                stage = "Docker Compose config";
                await Execute("docker", [.. prefix, "config", "--quiet"]);
                stage = "Docker Compose up";
                await Execute("docker", [.. prefix, "up", "-d", "--build", "--wait", "--wait-timeout", "180"]);
                await output.WriteLineAsync($"Local {environment} is ready: project {project}, bind {bind}.");
                await output.WriteLineAsync($"Gateway: http://{bind}; API: http://{bind}:5000; dashboard: http://{bind}:18888.");
            }
            else
            {
                stage = "Docker Compose down";
                await Execute("docker", [.. prefix, "down"]);
                await output.WriteLineAsync($"Local {environment} stopped: project {project}. Volumes, blobs, identity and keys preserved.");
            }
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync($"Local operation cancelled during {stage}; owned child stopped. Compose resources and data may remain. Stop with: wf local down --env {environment}");
            return 130;
        }
        catch (ArgumentException)
        {
            await error.WriteLineAsync(Usage);
            return 2;
        }
        catch (LocalFailure failure)
        {
            await error.WriteLineAsync($"Local {stage} failed: {failure.Message} No volumes or keys were removed. Stop remaining resources with: wf local down --env {environment}");
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or NotSupportedException)
        {
            await error.WriteLineAsync($"Local {stage} failed ({exception.GetType().Name}). Check checkout paths, private state permissions and required aspire/docker executables. No volumes or keys were removed. Stop remaining resources with: wf local down --env {environment}");
            return 2;
        }
    }

    private static string FindRepository(string workingDirectory)
    {
        RejectLinks(workingDirectory);
        for (var current = new DirectoryInfo(workingDirectory); current is not null; current = current.Parent)
        {
            var appHost = Path.Combine(current.FullName, "backend", "src", "Wf.AppHost", "Wf.AppHost.csproj");
            var composeOverride = Path.Combine(current.FullName, "infra", "local", "compose.override.yml");
            if (!File.Exists(appHost) || !File.Exists(composeOverride)) continue;
            RejectLinks(appHost);
            RejectLinks(composeOverride);
            return current.FullName;
        }
        throw new LocalFailure("A workflow-scheduler source checkout is required (backend/src/Wf.AppHost/Wf.AppHost.csproj and infra/local/compose.override.yml).");
    }

    private static FileStream AcquireLock(string path)
    {
        RejectLinks(path);
        FileStream stream;
        try { stream = OpenPrivate(path, FileMode.OpenOrCreate); }
        catch (IOException) { throw new LocalFailure("Environment is already locked or its operation.lock is unavailable; wait for the other wf local operation to finish."); }
        try
        {
            ProtectFile(path);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void ValidateCompose(string path)
    {
        RejectLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new LocalFailure("Saved publish/docker-compose.yaml is missing or empty; run wf local up with the same --env to publish it.");
    }

    private static async Task CreateRuntime(string path, CancellationToken token)
    {
        var staging = Path.Combine(Path.GetDirectoryName(path)!, ".runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var file = OpenPrivate(staging, FileMode.CreateNew))
            {
                ProtectFile(staging);
                var text = $"POSTGRES_PASSWORD={Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))}\n" +
                    $"STORAGE_KEY={Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}\n" +
                    $"AGENT_ID={Guid.NewGuid():D}\n";
                await file.WriteAsync(Utf8.GetBytes(text), token);
                await file.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            RejectLinks(path);
            File.Move(staging, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    private static async Task<Dictionary<string, string>> ReadRuntime(string path, CancellationToken token)
    {
        RejectLinks(path);
        ValidatePrivate(path, directory: false);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is 0 or > 1024) throw new LocalFailure("runtime.env is corrupt; restore the original private three-key file. Keys were not regenerated.");
        var bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes, token);
        if (await file.ReadAsync(new byte[1], token) != 0)
            throw new LocalFailure("runtime.env changed during validation; retry after stopping the writer.");
        string text;
        try { text = Utf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new LocalFailure("runtime.env must be valid UTF-8 with exactly the three stable keys."); }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var line in lines.Take(lines.Length - (lines[^1].Length == 0 ? 1 : 0)))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0 || !values.TryAdd(line[..separator], line[(separator + 1)..]))
                throw new LocalFailure("runtime.env has invalid or duplicate entries; restore the original file. Keys were not regenerated.");
        }
        if (values.Count != 3 ||
            !values.TryGetValue("POSTGRES_PASSWORD", out var password) || password.Length != 64 ||
            password.Any(character => !char.IsAsciiHexDigitLower(character)) ||
            !values.TryGetValue("STORAGE_KEY", out var storage) || storage.Length != 44 ||
            !Convert.TryFromBase64String(storage, new byte[32], out var length) || length != 32 ||
            Convert.ToBase64String(Convert.FromBase64String(storage)) != storage ||
            !values.TryGetValue("AGENT_ID", out var agent) || !Guid.TryParseExact(agent, "D", out var id) ||
            id == Guid.Empty || id.ToString("D") != agent)
            throw new LocalFailure("runtime.env requires exactly valid POSTGRES_PASSWORD (64 lowercase hex), STORAGE_KEY (32-byte base64), AGENT_ID (canonical nonempty UUID). Restore the original file; keys were not regenerated.");
        return values;
    }

    private static FileStream OpenPrivate(string path, FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileModeBits;
        return new FileStream(path, options);
    }

    private static void PrivateDirectory(string path, bool create)
    {
        RejectLinks(path);
        if (!Directory.Exists(path))
        {
            if (!create) throw new LocalFailure("Saved state directory is missing; run wf local up with the same --env first.");
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var user = identity.User ?? throw new UnauthorizedAccessException("Windows user identity is required.");
                var security = new DirectorySecurity();
                security.SetOwner(user);
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(path).Create(security);
            }
            else Directory.CreateDirectory(path, DirectoryMode);
        }
        ValidatePrivate(path, directory: true);
    }

    private static void ProtectFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new UnauthorizedAccessException("Windows user identity is required.");
            var security = new FileSecurity();
            security.SetOwner(user);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        ValidatePrivate(path, directory: false);
    }

    private static void ValidatePrivate(string path, bool directory)
    {
        RejectLinks(path);
        if (((File.GetAttributes(path) & FileAttributes.Directory) != 0) != directory)
            throw new LocalFailure("Local state must contain regular private files and directories.");
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new UnauthorizedAccessException("Windows user identity is required.");
            FileSystemSecurity security = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            if (security.GetOwner(typeof(SecurityIdentifier)) != user)
                throw new LocalFailure("Local state permissions must allow only the current owner.");
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference != user)
                    throw new LocalFailure("Local state permissions must allow only the current owner.");
        }
        else if (File.GetUnixFileMode(path) != (directory ? DirectoryMode : FileModeBits))
            throw new LocalFailure("Local state permissions must be owner-only: directories 0700, files 0600.");
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if (new FileInfo(current).LinkTarget is not null)
                throw new LocalFailure("Local checkout and state paths cannot contain symbolic links or reparse points.");
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new LocalFailure("Local checkout and state paths cannot contain symbolic links or reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void RejectTreeLinks(string directory)
    {
        RejectLinks(directory);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLinks(path);
            if (Directory.Exists(path)) RejectTreeLinks(path);
        }
    }

    internal static async Task<int> RunProcessAsync(ProcessStartInfo start, TextWriter output, TextWriter error,
        IReadOnlyList<string> secrets, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("Child process could not start.");
        async Task Forward(StreamReader source, TextWriter destination)
        {
            while (await source.ReadLineAsync() is { } line)
            {
                foreach (var secret in secrets) line = line.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
                await destination.WriteLineAsync(line);
            }
        }
        var stdout = Forward(process.StandardOutput, output);
        var stderr = Forward(process.StandardError, error);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            throw;
        }
        await Task.WhenAll(stdout, stderr);
        return process.ExitCode;
    }

    private sealed class LocalFailure(string message) : Exception(message);
}
