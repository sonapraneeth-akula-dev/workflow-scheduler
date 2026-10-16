using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Wf.Agent.Logs;
using Wf.Agent.Secrets;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Logs;

namespace Wf.Agent.Tests;

public sealed class LogSpoolAgentServiceTests
{
    [Theory]
    [InlineData(0UL, 1000L)]
    [InlineData(99UL, 99L)]
    [InlineData(ulong.MaxValue, 1000L)]
    public async Task Dispatch_cap_is_bounded_by_configured_ceiling_and_failed_upload_releases_closed_capture(ulong requested, long expected)
    {
        using var files = new LogFiles();
        var options = new AgentOptions([new Uri("http://localhost")], Guid.NewGuid().ToString(), "logs", "ci", 1,
            resultJournalDirectory: Path.Combine(files.Root, "results"), logSpoolDirectory: files.Root,
            maxLogBytes: 1000);
        await using var service = new AgentLogService(options, TimeProvider.System, NullLogger<AgentLogService>.Instance,
            NullLogger<LogChunkUploader>.Instance);
        var dispatch = new Dispatch { Task = LogSpoolTests.Attempt(), Limits = new ResourceLimits { MaxLogBytes = requested } };
        var capture = await service.BeginAsync(dispatch);
        await capture.WriteAsync("stdout", "closed capture");
        await capture.CompleteAsync();
        await capture.Upload.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.IsType<InvalidDataException>(service.LastFailure);
        var path = Path.Combine(files.Root, LogIndex.PrefixFor(dispatch.Task).Replace('/', Path.DirectorySeparatorChar), "index.json");
        var index = JsonSerializer.Deserialize(File.ReadAllBytes(path), LogJsonContext.Default.LogIndex)!;
        Assert.Equal(expected, index.MaxBytes);
        Assert.True(index.Complete);
        await using var reopened = new LogSpooler(files.Root, dispatch.Task, new SecretMasker([]));
        Assert.Equal(expected, (await reopened.SnapshotAsync(TestContext.Current.CancellationToken)).MaxBytes);
    }

    [Fact]
    public void Log_configuration_uses_explicit_paths_and_positive_invariant_byte_limits()
    {
        using var files = new LogFiles();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:Id"] = Guid.NewGuid().ToString(), ["Agent:Engines:0"] = "https://engine.example",
            ["Agent:LogSpoolDirectory"] = files.Root, ["Agent:MaxLogBytes"] = "1234",
            ["Agent:LogSpoolQuotaBytes"] = "5678"
        }).Build();
        var options = AgentOptions.FromConfiguration(configuration);
        Assert.Equal(files.Root, options.LogSpoolDirectory);
        Assert.Equal(1234, options.MaxLogBytes);
        Assert.Equal(5678, options.LogSpoolQuotaBytes);
        configuration["Agent:LogSpoolQuotaBytes"] = "-1";
        Assert.Throws<ArgumentException>(() => AgentOptions.FromConfiguration(configuration));
        configuration["Agent:LogSpoolQuotaBytes"] = "0";
        Assert.Throws<ArgumentException>(() => AgentOptions.FromConfiguration(configuration));
        configuration["Agent:LogSpoolQuotaBytes"] = "9223372036854775808";
        Assert.Throws<ArgumentException>(() => AgentOptions.FromConfiguration(configuration));
    }
}
