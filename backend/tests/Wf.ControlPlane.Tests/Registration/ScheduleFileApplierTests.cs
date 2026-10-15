using Wf.Contracts.Registration;
using Wf.ControlPlane.Registration;

namespace Wf.ControlPlane.Tests.Registration;

public sealed class ScheduleFileApplierTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/schedules.json")]
    [InlineData("../schedules.json")]
    [InlineData("dir/./schedules.json")]
    [InlineData("dir//schedules.json")]
    [InlineData("C:/schedules.json")]
    [InlineData("dir\\schedules.json")]
    [InlineData("dir/\nschedules.json")]
    public void Invalid_logical_source_paths_fail_with_structured_diagnostics(string? path)
    {
        var error = Assert.Throws<RegistrationValidationException>(() => ScheduleFileApplier.ValidateRequest(
            Request() with { ScheduleSourcePath = path }));
        Assert.Equal("$.scheduleSourcePath", Assert.Single(error.Errors).Path);
    }

    [Fact]
    public void Missing_identity_or_file_and_oversized_input_fail_explicitly()
    {
        Assert.Throws<RegistrationValidationException>(() => ScheduleFileApplier.ValidateRequest(Request() with { WorkflowId = null }));
        Assert.Throws<RegistrationValidationException>(() => ScheduleFileApplier.ValidateRequest(Request() with { Schedules = null }));
        Assert.Throws<RegistrationValidationException>(() => ScheduleFileApplier.ValidateRequest(Request() with { Schedules = new('x', 1024 * 1024 + 1) }));
        Assert.Throws<RegistrationValidationException>(() => ScheduleFileApplier.ValidateRequest(Request() with { Schedules = "\ud800" }));
        Assert.Throws<RegistrationValidationException>(() => ScheduleFileApplier.ValidateRequest(Request() with { Schedules = "\0" }));
        Assert.Throws<RegistrationValidationException>(() => ScheduleFileApplier.ValidateRequest(Request() with { ScheduleSourcePath = new('x', 1025) }));
    }

    [Fact]
    public void Optional_file_and_valid_repository_paths_pass()
    {
        ScheduleFileApplier.ValidateRequest(new(new('a', 40), "Author"));
        ScheduleFileApplier.ValidateRequest(Request());
    }

    private static RegistrationRequest Request() => new(new('a', 40), "Author", "team/type/workflow",
        Schedules: """{"instances":[]}""", ScheduleSourcePath: "dir/schedules.json");
}
