using Wf.Cli.Commands;

namespace Wf.Cli.Tests;

public sealed class AbortCommandTests : IDisposable
{
    private readonly string? engine = Environment.GetEnvironmentVariable("WF_ENGINE_URL");
    private readonly string? token = Environment.GetEnvironmentVariable("WF_ACCESS_TOKEN");
    private static readonly string Run = Guid.CreateVersion7().ToString("D");

    public AbortCommandTests()
    {
        Environment.SetEnvironmentVariable("WF_ENGINE_URL", "http://127.0.0.1:1");
        Environment.SetEnvironmentVariable("WF_ACCESS_TOKEN", null);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-run --reason stop")]
    [InlineData("00000000-0000-0000-0000-000000000000 --reason stop")]
    [InlineData("00000000-0000-4000-8000-000000000000 --reason stop")]
    [InlineData("$run")]
    [InlineData("$run --reason")]
    [InlineData("$run --reason stop --reason again")]
    [InlineData("$run --reason stop --engine https://one.example --engine https://two.example")]
    [InlineData("$run --reason stop --token secret-must-not-echo")]
    [InlineData("$run --reason stop extra")]
    [InlineData("$run --reason stop --unknown value")]
    [InlineData("$run --reason stop --engine http://remote.example")]
    [InlineData("$run --reason stop --engine https://user:secret-must-not-echo@example.test")]
    [InlineData("$run --reason stop --engine https://example.test/path")]
    [InlineData("$run --reason stop --engine https://example.test?token=secret-must-not-echo")]
    [InlineData("$run --reason stop --engine https://example.test#secret-must-not-echo")]
    [InlineData("$run --reason stop --engine file:///C:/private")]
    public async Task Abort_usage_and_trust_boundary_errors_do_not_send_requests_or_echo_input(string arguments)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var result = await AbortCommand.RunAsync(arguments.Replace("$run", Run, StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries), output, error, TestContext.Current.CancellationToken);
        Assert.Equal(2, result);
        Assert.Empty(output.ToString());
        Assert.Contains("Usage: wf abort", error.ToString());
        Assert.DoesNotContain("secret-must-not-echo", error.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task Abort_reason_boundaries_are_checked_before_connecting(int length)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await AbortCommand.RunAsync([Run, "--reason", new string('x', length)], output, error, TestContext.Current.CancellationToken));
        Assert.Contains("1-500", error.ToString());
        Assert.Empty(output.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("token with spaces")]
    [InlineData("secret-must-not-echo\r\nX-Injected:value")]
    public async Task Abort_remote_authentication_is_explicit_and_never_logged(string? value)
    {
        Environment.SetEnvironmentVariable("WF_ACCESS_TOKEN", value);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await AbortCommand.RunAsync([Run, "--reason", "stop", "--engine", "https://example.test"], output, error,
            TestContext.Current.CancellationToken));
        Assert.Contains("WF_ACCESS_TOKEN", error.ToString());
        Assert.DoesNotContain("secret-must-not-echo", error.ToString());
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task Abort_pre_cancelled_execution_is_not_reported_as_acceptance()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(130, await AbortCommand.RunAsync([Run, "--reason", new string('x', 500)], output, error, cancellation.Token));
        Assert.Empty(output.ToString());
        Assert.Contains("already accepted abort continues", error.ToString());
    }

    [Fact]
    public async Task Abort_help_requires_no_engine_or_authentication()
    {
        Environment.SetEnvironmentVariable("WF_ENGINE_URL", null);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await AbortCommand.RunAsync(["--help"], output, error, TestContext.Current.CancellationToken));
        Assert.Contains("WF_ACCESS_TOKEN", output.ToString());
        Assert.Empty(error.ToString());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("WF_ENGINE_URL", engine);
        Environment.SetEnvironmentVariable("WF_ACCESS_TOKEN", token);
    }
}
