using System.Collections.Immutable;
using Wf.Validation.SemVer;

namespace Wf.Validation.Schedules;

/// <summary>
/// Checks a schedule against the variable API of its target workflow version.
/// Node identities and implementation details are intentionally not inputs to schedule compatibility.
/// The caller selects the target API when resolving a version pin.
/// </summary>
public static class ScheduleCompatibilityRule
{
    public static ImmutableArray<ValidationError> Validate(ReadOnlyMemory<byte> schedules, ApiBaseline target,
        string file = ScheduleFileValidator.FileName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(target);
        return ScheduleFileValidator.Validate(schedules, target.Variables, file, cancellationToken).Errors;
    }
}
