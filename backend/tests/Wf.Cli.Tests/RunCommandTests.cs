using Wf.Cli.Commands;

namespace Wf.Cli.Tests;

public sealed class RunCommandTests
{
    [Theory]
    [InlineData("")]
    [InlineData("bad")]
    [InlineData("team/type/name --machine worker")]
    [InlineData("team/type/name --single-machine --single-machine")]
    [InlineData("team/type/name --single-machine --machine")]
    [InlineData("team/type/name --engine http://remote.invalid")]
    [InlineData("team/type/name --engine https://remote.invalid/path")]
    [InlineData("team/type/name --engine https://remote.invalid?secret=never-echo")]
    [InlineData("team/type/name --engine https://user:never-echo@remote.invalid")]
    [InlineData("team/type/name --var x=1 --var x=2")]
    [InlineData("team/type/name --unknown never-echo")]
    public async Task Invalid_run_options_fail_without_submission_or_secret_echo(string arguments)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var args = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, await RunCommand.RunAsync(args, output, error, TestContext.Current.CancellationToken));
        Assert.Empty(output.ToString());
        Assert.Contains(RunCommand.Usage, error.ToString());
        Assert.DoesNotContain("never-echo", error.ToString());
    }

    [Fact]
    public async Task Run_help_documents_noninteractive_submission_and_single_machine_options()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await RunCommand.RunAsync(["--help"], output, error, TestContext.Current.CancellationToken));
        Assert.Contains("--single-machine", output.ToString());
        Assert.Contains("WF_ACCESS_TOKEN", output.ToString());
        Assert.Empty(error.ToString());
    }
}
