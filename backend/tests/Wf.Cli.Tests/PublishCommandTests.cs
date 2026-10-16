using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wf.Cli.Commands;
using Wf.Contracts.Registration;
using Wf.Validation.SemVer;

namespace Wf.Cli.Tests;

[Collection("Publish")]
public sealed class PublishCommandTests(PublishFixture fixture)
{
    [Theory]
    [InlineData("")]
    [InlineData("--unknown")]
    [InlineData("directory")]
    [InlineData("directory --engine")]
    [InlineData("directory --engine --git-author author")]
    [InlineData("directory --engine http://localhost --engine http://localhost")]
    [InlineData("directory --git-author first --git-author second")]
    [InlineData("directory --git-commit first --git-commit second")]
    [InlineData("directory --work-directory one --work-directory two")]
    [InlineData("directory --baseline one --baseline two")]
    [InlineData("directory --team-type-quota 1 --team-type-quota 2")]
    [InlineData("directory --schedule-source-path one --schedule-source-path two")]
    [InlineData("directory --change-note one --change-note two")]
    [InlineData("directory --token secret-must-not-echo")]
    [InlineData("directory --package secret-must-not-echo")]
    [InlineData("directory --unknown secret-must-not-echo")]
    [InlineData("directory trailing")]
    public async Task Publish_invalid_arguments_are_explicit_and_do_not_echo_secrets(string text)
    {
        var result = await PublishFixture.Run(text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        AssertFailure(result, 2);
        Assert.Contains("Usage: wf publish", result.Error);
        Assert.DoesNotContain("secret-must-not-echo", result.Error);
    }

    [Theory]
    [InlineData("http://remote.example")]
    [InlineData("ftp://localhost")]
    [InlineData("file:///C:/workflow")]
    [InlineData("https://user:secret-must-not-echo@example.test")]
    [InlineData("https://example.test/path")]
    [InlineData("https://example.test/a/..")]
    [InlineData("https://example.test?token=secret-must-not-echo")]
    [InlineData("https://example.test#secret-must-not-echo")]
    [InlineData("https://example.test\\private")]
    [InlineData("https://example.test/./")]
    [InlineData(" https://example.test")]
    [InlineData("https://example.test ")]
    [InlineData("https://example.test\r\nInjected:secret-must-not-echo")]
    [InlineData("https:example.test")]
    public async Task Publish_engine_requires_an_explicit_safe_origin(string origin)
    {
        var result = await PublishFixture.Run(fixture.Arguments(origin));
        AssertFailure(result, 2);
        Assert.Contains("--engine", result.Error);
        Assert.DoesNotContain("secret-must-not-echo", result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("token with spaces")]
    [InlineData("secret-must-not-echo\r\nX-Injected:value")]
    [InlineData("tökén")]
    [InlineData("\u007f")]
    public async Task Publish_requires_safe_explicit_auth_even_on_loopback(string? token)
    {
        Environment.SetEnvironmentVariable("WF_ACCESS_TOKEN", token);
        try
        {
            var result = await PublishFixture.Run(fixture.Arguments("http://localhost"));
            AssertFailure(result, 2);
            Assert.Contains("WF_ACCESS_TOKEN", result.Error);
            Assert.DoesNotContain("secret-must-not-echo", result.Error);
        }
        finally { Environment.SetEnvironmentVariable("WF_ACCESS_TOKEN", PublishFixture.AccessToken); }
    }

    [Fact]
    public async Task Publish_token_length_limit_is_checked_before_local_build_or_network()
    {
        Environment.SetEnvironmentVariable("WF_ACCESS_TOKEN", new string('x', 16 * 1024 + 1));
        try
        {
            var result = await PublishFixture.Run(fixture.Arguments("http://localhost"));
            AssertFailure(result, 2);
            Assert.Contains("16 KiB", result.Error);
        }
        finally { Environment.SetEnvironmentVariable("WF_ACCESS_TOKEN", PublishFixture.AccessToken); }
    }

    [Theory]
    [InlineData("--git-commit", "A", "lowercase")]
    [InlineData("--git-commit", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaA", "lowercase")]
    [InlineData("--git-commit", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "lowercase")]
    [InlineData("--git-author", "author\r\nInjected", "--git-author")]
    [InlineData("--team-type-quota", "0", "positive integer")]
    [InlineData("--team-type-quota", "-1", "positive integer")]
    [InlineData("--team-type-quota", "1.5", "positive integer")]
    [InlineData("--team-type-quota", "2147483648", "positive integer")]
    [InlineData("--schedule-source-path", "../schedules.json", "repository-relative")]
    [InlineData("--schedule-source-path", "/schedules.json", "repository-relative")]
    [InlineData("--schedule-source-path", "teams//schedules.json", "repository-relative")]
    [InlineData("--schedule-source-path", "C:\\schedules.json", "repository-relative")]
    [InlineData("--schedule-source-path", "teams\\schedules.json", "repository-relative")]
    public async Task Publish_provenance_quota_and_schedule_path_boundaries_are_explicit(string option, string value, string diagnostic)
    {
        var arguments = Replace(fixture.Arguments("http://localhost"), option, value);
        var result = await PublishFixture.Run(arguments);
        AssertFailure(result, 2);
        Assert.Contains(diagnostic, result.Error);
    }

    [Theory]
    [InlineData("--git-author", 513, "--git-author")]
    [InlineData("--change-note", 4001, "4000")]
    [InlineData("--schedule-source-path", 1025, "repository-relative")]
    [InlineData("--engine", 2049, "--engine")]
    public async Task Publish_option_size_limits_precede_compilation(string option, int length, string diagnostic)
    {
        var result = await PublishFixture.Run(Replace(fixture.Arguments("http://localhost"), option, new string('x', length)));
        AssertFailure(result, 2);
        Assert.Contains(diagnostic, result.Error);
    }

    [Fact]
    public async Task Publish_arguments_have_count_utf8_total_and_unicode_limits()
    {
        foreach (var arguments in new[]
        {
            new[] { new string('x', 4097) },
            new[] { fixture.Source, "--change-note", "\ud800" },
            Enumerable.Repeat("x", 257).ToArray(),
            new[] { fixture.Source }.Concat(Enumerable.Range(0, 40).SelectMany(_ => new[] { "--input", new string('é', 1000) })).ToArray()
        })
            AssertFailure(await PublishFixture.Run(arguments), 2);
    }

    [Theory]
    [InlineData("--input", "https://example.test/source")]
    [InlineData("--input", "\\\\server\\share\\source")]
    [InlineData("--work-directory", "https://example.test/cache")]
    [InlineData("--baseline", "\\\\server\\share\\workflow.api.json")]
    public async Task Publish_source_closure_and_output_paths_must_be_local(string option, string path)
    {
        var result = await PublishFixture.Run(Replace(fixture.Arguments("http://localhost"), option, path));
        AssertFailure(result, 2);
        Assert.Contains("local filesystem paths", result.Error);
    }

    [Fact]
    public async Task Publish_cancelled_before_build_does_not_claim_acceptance()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var result = await PublishFixture.Run(fixture.Arguments("http://localhost"), cancel.Token);
        AssertFailure(result, 130);
        Assert.Contains("registration was not submitted", result.Error);
    }

    [Fact]
    public async Task Publish_help_and_program_routing_preserve_validate_abort_and_no_auth_help()
    {
        Environment.SetEnvironmentVariable("WF_ACCESS_TOKEN", null);
        try
        {
            var help = await PublishFixture.Run(["--help"]);
            Assert.Equal(0, help.Exit);
            Assert.Contains("WF_ACCESS_TOKEN", help.Output);
            Assert.Empty(help.Error);
            var routed = await PublishFixture.Process(["--help"]);
            Assert.Equal(0, routed.Exit);
            foreach (var command in new[] { "validate", "abort", "publish" })
                Assert.Contains("Usage: wf " + command, routed.Output);
            Assert.Empty(routed.Error);
            var invalid = await PublishFixture.Process(["publish"]);
            AssertFailure(invalid, 2);
            Assert.Contains("Usage: wf publish", invalid.Error);
        }
        finally { Environment.SetEnvironmentVariable("WF_ACCESS_TOKEN", PublishFixture.AccessToken); }
    }

    [Fact]
    public async Task Publish_invalid_workflow_diagnostics_equal_ci_validation()
    {
        var previous = await File.ReadAllBytesAsync(fixture.Registry, PublishFixture.Token);
        try
        {
            await File.WriteAllTextAsync(fixture.Registry, "{}", PublishFixture.Token);
            using var output = new StringWriter();
            using var error = new StringWriter();
            var ci = await ValidateCommand.RunAsync(fixture.Common, output, error, PublishFixture.Token);
            var result = await PublishFixture.Run(fixture.Arguments("http://127.0.0.1:1"));
            Assert.Equal(1, ci);
            AssertFailure(result, 1);
            Assert.Equal(error.ToString(), result.Error);
            Assert.Contains("WFV015", result.Error);
        }
        finally { await File.WriteAllBytesAsync(fixture.Registry, previous, PublishFixture.Token); }
    }

    [Fact]
    public async Task Publish_missing_inputs_keep_shared_missing_workflow_diagnostics()
    {
        var empty = fixture.NewFile("empty");
        Directory.CreateDirectory(empty);
        try
        {
            foreach (var (directory, rule) in new[] { (empty, "WFV002"), (fixture.NewFile("missing"), "WFV001") })
            {
                var arguments = fixture.Arguments("http://localhost");
                arguments[0] = directory;
                var result = await PublishFixture.Run(arguments);
                AssertFailure(result, 1);
                Assert.Contains(rule, result.Error);
            }
        }
        finally { Directory.Delete(empty); }
    }

    [Fact]
    public async Task Publish_registry_ownership_cannot_be_silently_inferred_from_its_type()
    {
        var previous = await File.ReadAllBytesAsync(fixture.Registry, PublishFixture.Token);
        try
        {
            await File.WriteAllTextAsync(fixture.Registry, PublishFixture.RegistryJson("another-team"), PublishFixture.Token);
            var result = await PublishFixture.Run(fixture.Arguments("http://127.0.0.1:1"));
            AssertFailure(result, 2);
            Assert.Contains("ownership", result.Error);
        }
        finally { await File.WriteAllBytesAsync(fixture.Registry, previous, PublishFixture.Token); }
    }

    [Fact]
    public async Task Publish_baseline_classification_equals_ci_without_network()
    {
        var baseline = fixture.NewFile("workflow.api.json");
        await File.WriteAllBytesAsync(baseline, ApiBaseline.FromManifest(fixture.Build.Manifest).WriteUtf8(), PublishFixture.Token);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await ValidateCommand.RunAsync([.. fixture.Common, "--baseline", baseline], output, error, PublishFixture.Token));
        var result = await PublishFixture.Run(fixture.Arguments("http://127.0.0.1:1", "--baseline", baseline));
        AssertFailure(result, 1);
        Assert.Equal(error.ToString(), result.Error);
        Assert.Contains("WFV010", result.Error);
    }

    [Theory]
    [InlineData("workflow.cs", "invalid-utf8")]
    [InlineData("workflow.cs", "oversize")]
    [InlineData("team.json", "invalid-utf8")]
    [InlineData("team.json", "oversize")]
    [InlineData("schedules.json", "invalid-utf8")]
    [InlineData("schedules.json", "oversize")]
    [InlineData("schedules.json", "nul")]
    public async Task Publish_text_trust_boundaries_reject_before_compilation(string file, string invalid)
    {
        var path = file == "team.json" ? fixture.Registry : Path.Combine(fixture.Source, file);
        var previous = File.Exists(path) ? await File.ReadAllBytesAsync(path, PublishFixture.Token) : null;
        try
        {
            await File.WriteAllBytesAsync(path, invalid switch
            {
                "invalid-utf8" => [0xc3, 0x28],
                "oversize" => new byte[1024 * 1024 + 1],
                _ => [0]
            }, PublishFixture.Token);
            var result = await PublishFixture.Run(file == "schedules.json"
                ? fixture.Arguments("http://localhost", "--schedule-source-path", "teams/tests/release/publish/schedules.json")
                : fixture.Arguments("http://localhost"));
            AssertFailure(result, 2);
            Assert.True(result.Error.Contains("UTF-8", StringComparison.Ordinal) ||
                result.Error.Contains("1048576", StringComparison.Ordinal) || result.Error.Contains("NUL", StringComparison.Ordinal), result.Error);
        }
        finally
        {
            if (previous is null) File.Delete(path);
            else await File.WriteAllBytesAsync(path, previous, PublishFixture.Token);
        }
    }

    [Fact]
    public async Task Publish_schedule_path_requires_an_actual_file_and_never_implicitly_assigns_provenance()
    {
        var absent = await PublishFixture.Run(fixture.Arguments("http://localhost", "--schedule-source-path",
            "teams/tests/release/publish/schedules.json"));
        AssertFailure(absent, 2);
        Assert.Contains("local schedules.json", absent.Error);
        var path = Path.Combine(fixture.Source, "schedules.json");
        try
        {
            await File.WriteAllTextAsync(path, "{}", PublishFixture.Token);
            var implicitPath = await PublishFixture.Run(fixture.Arguments("http://localhost"));
            AssertFailure(implicitPath, 2);
            Assert.Contains("requires --schedule-source-path", implicitPath.Error);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(409)]
    [InlineData(413)]
    [InlineData(500)]
    public async Task Publish_grant_rejection_does_not_redirect_retry_upload_or_register(int status)
    {
        await using var server = new PublishHttpServer(_ => new(status, Location: "/secret-must-not-echo"));
        var result = await PublishFixture.Run(fixture.Arguments(server.Origin));
        AssertFailure(result, 1);
        Assert.Contains("HTTP " + status, result.Error);
        Assert.Single(server.Requests);
        Assert.DoesNotContain(PublishFixture.AccessToken, result.Error);
    }

    [Theory]
    [InlineData("wrong-status")]
    [InlineData("exists-with-uri")]
    [InlineData("missing-uri")]
    [InlineData("different-size")]
    [InlineData("expired")]
    [InlineData("cross-origin")]
    [InlineData("network-path")]
    [InlineData("dot-segments")]
    [InlineData("empty-capability")]
    [InlineData("query")]
    [InlineData("invalid-json")]
    [InlineData("duplicate-property")]
    [InlineData("unknown-property")]
    [InlineData("oversized-body")]
    public async Task Publish_malformed_grants_never_upload_or_register(string invalid)
    {
        await using var server = new PublishHttpServer(request =>
        {
            var grant = JsonSerializer.Deserialize(request.Body, RegistrationJsonContext.Default.UploadGrantRequest)!;
            var uri = invalid switch
            {
                "cross-origin" => "https://other.example/api/v1/uploads/payload.signature",
                "network-path" => "//other.example/api/v1/uploads/payload.signature",
                "dot-segments" => "/api/v1/uploads/../registrations",
                "empty-capability" => "/api/v1/uploads/.",
                "query" => "/api/v1/uploads/payload.signature?token=secret-must-not-echo",
                _ => "/api/v1/uploads/payload.signature"
            };
            if (invalid == "wrong-status") return new(200, Grant(grant.Size));
            if (invalid == "exists-with-uri") return new(200, """{"exists":true,"uploadUri":"/api/v1/uploads/payload.signature"}""");
            if (invalid == "missing-uri") return new(201, """{"exists":false}""");
            if (invalid == "invalid-json") return new(201, "not-json");
            if (invalid == "duplicate-property") return new(201, """{"exists":true,"exists":false}""");
            if (invalid == "unknown-property") return new(201, """{"exists":true,"unexpected":1}""");
            if (invalid == "oversized-body") return new(201, new string('x', 64 * 1024 + 1));
            return new(201, Grant(invalid == "different-size" ? grant.Size + 1 : grant.Size, uri, invalid == "expired"));
        });
        var result = await PublishFixture.Run(fixture.Arguments(server.Origin));
        AssertFailure(result, 2);
        Assert.Single(server.Requests);
        Assert.DoesNotContain(PublishFixture.AccessToken, result.Error);
    }

    [Fact]
    public async Task Publish_actual_process_uploads_exact_independently_built_ci_package_sha_and_explicit_registration()
    {
        var ciPath = fixture.NewFile("ci.zip");
        var ciArguments = Replace(fixture.Common, "--work-directory", Path.Combine(fixture.Root, "ci-work"));
        var ci = await PublishFixture.Process(["validate", .. ciArguments, "--package", ciPath]);
        Assert.Equal(0, ci.Exit);
        Assert.Empty(ci.Error);
        var expected = await File.ReadAllBytesAsync(ciPath, PublishFixture.Token);
        var hash = Convert.ToHexStringLower(SHA256.HashData(expected));
        var version = Guid.CreateVersion7();
        await using var server = new PublishHttpServer(request => request.Path switch
        {
            "/api/v1/upload-grants" => new(201, Grant(expected.Length)),
            "/api/v1/uploads/payload.signature" => new(201),
            "/api/v1/registrations" => new(201, $$"""{"workflowVersion":"{{version:D}}"}"""),
            _ => new(404)
        });
        var result = await PublishFixture.Process(["publish", .. fixture.Arguments(server.Origin, "--change-note", "Explicit note")]);
        Assert.Equal(0, result.Exit);
        Assert.Empty(result.Error);
        Assert.Contains("Registered: tests/release/publish@1.0.0", result.Output);
        Assert.Contains("SHA256: " + hash, result.Output);
        var requests = server.Requests.ToArray();
        Assert.Equal(["POST", "PUT", "POST"], requests.Select(request => request.Method).ToArray());
        Assert.Equal("Bearer " + PublishFixture.AccessToken, requests[0].Headers["Authorization"]);
        Assert.Equal("Bearer " + PublishFixture.AccessToken, requests[2].Headers["Authorization"]);
        Assert.False(requests[1].Headers.ContainsKey("Authorization"));
        Assert.Equal(expected, requests[1].Body);
        var grant = JsonSerializer.Deserialize(requests[0].Body, RegistrationJsonContext.Default.UploadGrantRequest)!;
        Assert.Equal("package", grant.Kind);
        Assert.Equal(hash, grant.Sha256);
        Assert.Equal(expected.Length, grant.Size);
        var registration = JsonSerializer.Deserialize(requests[2].Body, RegistrationJsonContext.Default.RegistrationRequest)!;
        Assert.Equal(new PackageReference(hash, expected.Length), registration.Package);
        Assert.Equal("tests/release/publish", registration.WorkflowId);
        Assert.Equal(new string('a', 40), registration.GitCommit);
        Assert.Equal("Publish Unit Author", registration.GitAuthor);
        Assert.Equal("Explicit note", registration.ChangeNote);
        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(fixture.Source, "workflow.cs"), PublishFixture.Token), registration.Source);
        Assert.Equal("tests", registration.Registry!.Value.GetProperty("team").GetString());
        Assert.Null(registration.Schedules);
        Assert.Null(registration.ScheduleSourcePath);
        Assert.DoesNotContain(PublishFixture.AccessToken, result.Output);
        Assert.Empty(Directory.GetDirectories(fixture.Work, "p-*"));
    }

    [Fact]
    public async Task Publish_existing_package_skips_upload_and_unchanged_receipt_is_explicit()
    {
        await using var server = new PublishHttpServer(request =>
            request.Path == "/api/v1/upload-grants" ? new(200, """{"exists":true}""") : new(200));
        var result = await PublishFixture.Run(fixture.Arguments(server.Origin));
        Assert.Equal(0, result.Exit);
        Assert.Empty(result.Error);
        Assert.Contains("Already registered:", result.Output);
        Assert.Equal(["/api/v1/upload-grants", "/api/v1/registrations"],
            server.Requests.Select(request => request.Path).ToArray());
    }

    [Theory]
    [InlineData(200)]
    [InlineData(202)]
    [InlineData(204)]
    [InlineData(307)]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task Publish_upload_rejection_does_not_register_or_retry(int status)
    {
        await using var server = new PublishHttpServer(request =>
            request.Path == "/api/v1/upload-grants"
                ? new(201, Grant(JsonSerializer.Deserialize(request.Body, RegistrationJsonContext.Default.UploadGrantRequest)!.Size))
                : new(status));
        var result = await PublishFixture.Run(fixture.Arguments(server.Origin));
        AssertFailure(result, 1);
        Assert.Contains("Package upload rejected: HTTP " + status, result.Error);
        Assert.Equal(2, server.Requests.Count);
        Assert.False(server.Requests.Last().Headers.ContainsKey("Authorization"));
        Assert.Empty(Directory.GetDirectories(fixture.Work, "p-*"));
    }

    [Theory]
    [InlineData(201, "{}")]
    [InlineData(200, "{\"workflowVersion\":\"00000000-0000-7000-8000-000000000001\"}")]
    [InlineData(201, "{\"workflowVersion\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData(201, "{\"workflowVersion\":\"00000000-0000-4000-8000-000000000001\"}")]
    [InlineData(201, "{\"workflowVersion\":\"not-a-guid\"}")]
    [InlineData(201, "{\"workflowVersion\":null}")]
    [InlineData(201, "{\"unexpected\":\"value\"}")]
    [InlineData(200, "{\"workflowVersion\":null,\"workflowVersion\":null}")]
    [InlineData(201, "not-json")]
    public async Task Publish_invalid_success_receipts_are_not_false_success_or_retried(int status, string body)
    {
        await using var server = new PublishHttpServer(request =>
            request.Path == "/api/v1/upload-grants" ? new(200, """{"exists":true}""") : new(status, body));
        var result = await PublishFixture.Run(fixture.Arguments(server.Origin));
        AssertFailure(result, 2);
        Assert.Contains("may already be durable", result.Error);
        Assert.Equal(2, server.Requests.Count);
    }

    [Theory]
    [InlineData(202)]
    [InlineData(307)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(409)]
    [InlineData(413)]
    [InlineData(500)]
    public async Task Publish_registration_http_rejections_do_not_report_success_or_retry(int status)
    {
        await using var server = new PublishHttpServer(request =>
            request.Path == "/api/v1/upload-grants" ? new(200, """{"exists":true}""") : new(status));
        var result = await PublishFixture.Run(fixture.Arguments(server.Origin));
        AssertFailure(result, 1);
        Assert.Contains("Registration rejected: HTTP " + status, result.Error);
        Assert.Equal(2, server.Requests.Count);
    }

    [Theory]
    [InlineData(PublishFixture.AccessToken)]
    [InlineData("token+with/slash=and\"quote")]
    public async Task Publish_remote_validation_diagnostics_are_structured_but_never_echo_bearer_token(string token)
    {
        Environment.SetEnvironmentVariable("WF_ACCESS_TOKEN", token);
        try
        {
            var problem = JsonSerializer.Serialize(new
            {
                errors = new[] { new { ruleId = "WFV010", file = "workflow.cs", path = "$.version",
                    message = "Version must increase", hint = token + " must not be logged" } }
            });
            await using var server = new PublishHttpServer(request =>
                request.Path == "/api/v1/upload-grants" ? new(200, """{"exists":true}""") : new(409, problem));
            var result = await PublishFixture.Run(fixture.Arguments(server.Origin));
            AssertFailure(result, 1);
            Assert.Contains("WFV010", result.Error);
            Assert.Contains("[redacted]", result.Error);
            Assert.DoesNotContain(token, result.Error);
            Assert.DoesNotContain(JsonEncodedText.Encode(token).ToString(), result.Error);
            Assert.Equal(2, server.Requests.Count);
        }
        finally { Environment.SetEnvironmentVariable("WF_ACCESS_TOKEN", PublishFixture.AccessToken); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publish_inflight_cancellation_reports_correct_registration_uncertainty(bool registering)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(PublishFixture.Token);
        await using var server = new PublishHttpServer(request =>
        {
            if (request.Path == "/api/v1/upload-grants" && registering) return new(200, """{"exists":true}""");
            cancellation.Cancel();
            return new(200, Drop: true);
        });
        var result = await PublishFixture.Run(fixture.Arguments(server.Origin), cancellation.Token);
        AssertFailure(result, 130);
        Assert.Contains(registering ? "may already be durable" : "registration was not submitted", result.Error);
        Assert.Equal(registering ? 2 : 1, server.Requests.Count);
        Assert.Empty(Directory.GetDirectories(fixture.Work, "p-*"));
    }

    [Fact]
    public async Task Publish_registration_connection_loss_never_retries_or_claims_success()
    {
        await using var server = new PublishHttpServer(request =>
            request.Path == "/api/v1/upload-grants" ? new(200, """{"exists":true}""") : new(200, Drop: true));
        var result = await PublishFixture.Run(fixture.Arguments(server.Origin));
        AssertFailure(result, 2);
        Assert.Contains("may already be durable", result.Error);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task Publish_corrupt_validation_cache_cannot_register_stale_package()
    {
        var original = await File.ReadAllBytesAsync(fixture.Build.AssemblyPath, PublishFixture.Token);
        try
        {
            await File.WriteAllTextAsync(fixture.Build.AssemblyPath, "corrupted cache", PublishFixture.Token);
            var result = await PublishFixture.Run(fixture.Arguments("http://localhost"));
            AssertFailure(result, 2);
            Assert.Contains("integrity", result.Error);
        }
        finally { await File.WriteAllBytesAsync(fixture.Build.AssemblyPath, original, PublishFixture.Token); }
    }

    private static string[] Replace(string[] arguments, string option, string value)
    {
        var index = Array.IndexOf(arguments, option);
        if (index < 0) return [.. arguments, option, value];
        arguments[index + 1] = value;
        return arguments;
    }

    private static void AssertFailure((int Exit, string Output, string Error) result, int exit)
    {
        Assert.Equal(exit, result.Exit);
        Assert.Empty(result.Output);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    private static string Grant(long size, string path = "/api/v1/uploads/payload.signature", bool expired = false) =>
        JsonSerializer.Serialize(new UploadGrantResponse(false, path, size,
            DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 5)), RegistrationJsonContext.Default.UploadGrantResponse);
}
