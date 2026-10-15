using System.Runtime.CompilerServices;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Wf.Validation;
using Wf.Validation.Build;
using Wf.Validation.Schedules;

namespace Wf.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.Full]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 10, invocationCount: 1)]
public class ValidatorBench
{
    public const int NodeCount = 200;
    public const int ScheduleInstanceCount = 50;

    private string? root;
    private ScriptBuildCache? cache;
    private string[] additionalInputs = [];
    public string SourceDirectory { get; private set; } = "";

    [GlobalSetup]
    public async Task Setup()
    {
        if (root is not null)
            throw new InvalidOperationException("The validator benchmark is already initialized.");
        var repository = FindRepository();
        root = Path.Combine(repository, $".validator-bench-{Guid.NewGuid().ToString("N")[..8]}");
        SourceDirectory = Path.Combine(root, "s");
        cache = new ScriptBuildCache(Path.Combine(root, "c"), Path.Combine(root, "a"));
        // Contracts compiles protobuf files outside its project tree; include those inputs too.
        additionalInputs = new[] { "Wf.Sdk", "Wf.Contracts", "Wf.Engine.Core", "Wf.Validation" }
            .Select(name => Path.Combine(repository, "backend", "src", name))
            .Append(Path.Combine(repository, "backend", "proto")).ToArray();
        try
        {
            Directory.CreateDirectory(SourceDirectory);
            WriteWorkflow(repository);
            WriteSchedules();
            File.WriteAllText(Path.Combine(SourceDirectory, "team.json"), """
                {"team":"benchmark","types":[{"team":"benchmark","name":"validation",
                  "description":"Validator benchmark workflows",
                  "metadataSchema":{"keys":[{"key":"area","required":true,"allowedValues":["benchmark"]}],
                    "allowFreeForm":false}}]}
                """);
            var cold = await Validator.ValidateAsync(SourceDirectory, cache, additionalInputs);
            var build = RequireValidBuild(cold);
            if (build.CacheHit)
                throw new InvalidOperationException("The isolated fixture unexpectedly reused a previous build.");
            VerifyFixture(build);
            await ValidateCachedWorkflowAndSchedules();
            Console.WriteLine($"Validator warm-cache scope: {NodeCount} nodes, {ScheduleInstanceCount} schedules; " +
                "SDK/input hashing, cache-integrity reads, manifest/schedule/team semantics included; " +
                $"cold publish and --describe excluded. Fixture: {root}");
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    [Benchmark]
    public async Task<ValidationResult> ValidateCachedWorkflowAndSchedules()
    {
        if (cache is null)
            throw new InvalidOperationException("Run validator benchmark setup before measuring.");
        if (!File.Exists(Path.Combine(SourceDirectory, ScheduleFileValidator.FileName)))
            throw new InvalidDataException("The benchmark schedules.json is missing.");
        var result = await Validator.ValidateAsync(SourceDirectory, cache, additionalInputs);
        var build = RequireValidBuild(result);
        if (!build.CacheHit)
            throw new InvalidOperationException("Warm-cache validation unexpectedly compiled the workflow.");
        return result;
    }

    private static ScriptBuildResult RequireValidBuild(ValidationResult? result)
    {
        if (result is null || !result.IsValid)
            throw new InvalidOperationException("Validator benchmark failed: " +
                (result is null ? "no result." : string.Join("; ", result.Errors.Select(error =>
                    $"{error.RuleId} {error.File} {error.Path}: {error.Message}"))), result?.BuildFailure);
        var build = result.Build ?? throw new InvalidOperationException("Validator returned no build result.");
        if (build.Manifest.Nodes.Length != NodeCount || build.Manifest.Variables.Length != 5)
            throw new InvalidDataException("The benchmark requires exactly 200 nodes and five variable declarations.");
        return build;
    }

    private void VerifyFixture(ScriptBuildResult build)
    {
        for (var index = 0; index < NodeCount; index++)
        {
            var node = build.Manifest.Nodes[index];
            if (node.Key != $"node-{index:D3}" || node.Tasks.Length != 1 ||
                node.Tasks[0].Key != "work" || node.Dependencies.Length != (index == 0 ? 0 : 1) ||
                (index > 0 && node.Dependencies[0].Node != $"node-{index - 1:D3}"))
                throw new InvalidDataException("The benchmark workflow is not the expected 200-node chain.");
        }
        var schedules = ScheduleFileValidator.Validate(
            File.ReadAllBytes(Path.Combine(SourceDirectory, ScheduleFileValidator.FileName)), build.Manifest.Variables);
        if (!schedules.IsValid || schedules.Instances.Length != ScheduleInstanceCount)
            throw new InvalidDataException("The benchmark requires exactly 50 valid, resolved schedule instances.");
        for (var index = 0; index < ScheduleInstanceCount; index++)
        {
            var instance = schedules.Instances[index];
            var cron = index % 2 == 0;
            if (instance.Definition.Id != $"instance-{index:D2}" ||
                (instance.Definition.Cron is not null) != cron ||
                (instance.Expression.Interval is null) != cron ||
                instance.Variables["environment"].GetString() != (cron ? "prod" : "ppe") ||
                instance.Variables["count"].GetInt32() != (cron ? 2 : index + 1) ||
                instance.Variables["region"].GetString() != (cron ? "us-west" : "us-east") ||
                !instance.Variables["enabled"].GetBoolean() ||
                instance.Variables["timeout"].GetString() != "00:05:00")
                throw new InvalidDataException("The benchmark schedule expressions/defaults/overrides are invalid.");
        }
    }

    private void WriteWorkflow(string repository) =>
        File.WriteAllText(Path.Combine(SourceDirectory, "workflow.cs"), $$"""
            #:project {{Path.Combine(repository, "backend", "src", "Wf.Sdk", "Wf.Sdk.csproj")}}
            #:property PublishAot=false
            using Wf.Sdk;

            if (!args.Contains("--describe"))
                throw new InvalidOperationException("Benchmark workflows support --describe only.");
            var workflow = Workflow.Define("benchmark/validator", "1.0.0", w =>
            {
                w.Type("validation").Metadata("area", "benchmark").Variables(v => v
                    .String("environment", "dev", required: true, allowed: ["dev", "ppe", "prod"])
                    .String("region", required: true)
                    .Int("count", 1)
                    .Bool("enabled", true)
                    .Duration("timeout", TimeSpan.FromMinutes(5)));
                for (var index = 0; index < {{NodeCount}}; index++)
                {
                    var node = index;
                    w.Node($"node-{node:D3}", n =>
                    {
                        if (node > 0) n.DependsOn($"node-{node - 1:D3}");
                        n.Task("work", _ =>
                            throw new InvalidOperationException("Validation must never execute task delegates."));
                    });
                }
            });
            await ManifestWriter.WriteAsync(workflow, Console.OpenStandardOutput());
            """);

    private void WriteSchedules()
    {
        using var stream = File.Create(Path.Combine(SourceDirectory, ScheduleFileValidator.FileName));
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteStartObject("defaults");
        writer.WriteStartObject("deployment");
        writer.WriteString("environment", "ppe");
        writer.WriteString("region", "us-east");
        writer.WriteEndObject();
        writer.WriteStartObject("capacity");
        writer.WriteNumber("count", 2);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteStartArray("instances");
        for (var index = 0; index < ScheduleInstanceCount; index++)
        {
            var cron = index % 2 == 0;
            writer.WriteStartObject();
            writer.WriteString("id", $"instance-{index:D2}");
            writer.WriteString("timezone", cron ? "America/New_York" : "Etc/UTC");
            if (cron)
                writer.WriteString("cron", $"{index} 9 * * 1-5");
            else
                writer.WriteString("interval", TimeSpan.FromMinutes(index + 1).ToString("c"));
            writer.WriteString("versionPin", "1.0.0");
            writer.WriteStartObject("variables");
            if (cron)
            {
                writer.WriteString("environment", "prod");
                writer.WriteString("region", "us-west");
            }
            else
                writer.WriteNumber("count", index + 1);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static string FindRepository([CallerFilePath] string source = "")
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(source)!);
             directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "backend", "src", "Wf.Sdk", "Wf.Sdk.csproj")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Validator benchmarks require the repository source tree.");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (root is not null && Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        root = null;
        cache = null;
        SourceDirectory = "";
    }
}
