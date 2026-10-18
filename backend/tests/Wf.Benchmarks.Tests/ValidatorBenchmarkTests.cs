using System.Text.Json;
using System.Text.Json.Nodes;
using Wf.Validation;
using Wf.Validation.Schedules;

namespace Wf.Benchmarks.Tests;

public sealed class ValidatorBenchmarkTests
{
    [Fact]
    public async Task MeasuringWithoutSetupFailsExplicitly()
    {
        var benchmark = new ValidatorBench();
        await Assert.ThrowsAsync<InvalidOperationException>(benchmark.ValidateCachedWorkflowAndSchedules);
    }

    [Fact]
    public async Task RealFixtureHas200Nodes50ResolvedSchedulesAndWarmValidationChecksIntegrity()
    {
        var benchmark = new ValidatorBench();
        try
        {
            await benchmark.Setup();
            var result = await benchmark.ValidateCachedWorkflowAndSchedules();
            Assert.True(result.IsValid);
            var build = Assert.IsType<Wf.Validation.Build.ScriptBuildResult>(result.Build);
            Assert.True(build.CacheHit);
            Assert.Equal(TimeSpan.Zero, build.PublishDuration);
            Assert.Equal(TimeSpan.Zero, build.DescribeDuration);
            Assert.Equal(ValidatorBench.NodeCount, build.Manifest.Nodes.Length);
            Assert.Equal(200, build.Manifest.Nodes.Select(node => node.Key).Distinct().Count());
            Assert.Equal(199, build.Manifest.Nodes.Sum(node => node.Dependencies.Length));
            Assert.All(build.Manifest.Nodes, node => Assert.Equal("work", Assert.Single(node.Tasks).Key));

            var schedulePath = Path.Combine(benchmark.SourceDirectory, ScheduleFileValidator.FileName);
            var json = await File.ReadAllBytesAsync(schedulePath, TestContext.Current.CancellationToken);
            var schedules = ScheduleFileValidator.Validate(json, build.Manifest.Variables,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(schedules.IsValid);
            Assert.Equal(ValidatorBench.ScheduleInstanceCount, schedules.Instances.Length);
            Assert.Equal(50, schedules.Instances.Select(instance => instance.Definition.Id).Distinct().Count());
            Assert.Equal(25, schedules.Instances.Count(instance => instance.Definition.Cron is not null));
            Assert.Equal(25, schedules.Instances.Count(instance => instance.Expression.Interval is not null));
            Assert.Equal("prod", schedules.Instances[0].Variables["environment"].GetString());
            Assert.Equal(2, schedules.Instances[0].Variables["count"].GetInt32());
            Assert.Equal("ppe", schedules.Instances[1].Variables["environment"].GetString());
            Assert.Equal(4, schedules.Instances[3].Variables["count"].GetInt32());
            Assert.All(schedules.Instances, instance =>
            {
                Assert.Equal(5, instance.Variables.Count);
                Assert.True(instance.Variables["enabled"].GetBoolean());
                Assert.Equal("00:05:00", instance.Variables["timeout"].GetString());
            });
            var required = Assert.Single(build.Manifest.Variables, schema => schema.Key == "region");
            Assert.True(required.Required);
            Assert.Null(required.Default);

            var invalid = JsonNode.Parse(json)!.AsObject();
            invalid["instances"]![0]!["variables"]!["environment"] = "not-allowed";
            Assert.Contains(ScheduleFileValidator.Validate(
                JsonSerializer.SerializeToUtf8Bytes(invalid), build.Manifest.Variables,
                cancellationToken: TestContext.Current.CancellationToken).Errors,
                error => error.RuleId == RuleCatalog.ScheduleVariable.Id);

            File.Delete(schedulePath);
            await Assert.ThrowsAsync<InvalidDataException>(benchmark.ValidateCachedWorkflowAndSchedules);
            await File.WriteAllBytesAsync(schedulePath, json, TestContext.Current.CancellationToken);
            Assert.True((await benchmark.ValidateCachedWorkflowAndSchedules()).Build!.CacheHit);

            var manifestPath = Path.Combine(Path.GetDirectoryName(build.PublishDirectory)!, "manifest.json");
            await File.AppendAllTextAsync(manifestPath, " ", TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(benchmark.ValidateCachedWorkflowAndSchedules);
        }
        finally
        {
            var source = benchmark.SourceDirectory;
            benchmark.Cleanup();
            if (source.Length > 0)
                Assert.False(Directory.Exists(source));
        }
    }
}
