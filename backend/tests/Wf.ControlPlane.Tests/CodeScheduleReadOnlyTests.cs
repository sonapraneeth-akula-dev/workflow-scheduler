using System.Collections.Immutable;
using System.Text.Json;
using Wf.Contracts.Schedules;
using Wf.ControlPlane.Api;

namespace Wf.ControlPlane.Tests;

public sealed class CodeScheduleReadOnlyTests
{
    [Theory]
    [InlineData("Code", "nested/workflow.schedules.json", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("Code", null, null)]
    [InlineData("User", null, null)]
    public void Public_source_generated_response_preserves_origin_and_nullable_provenance(string origin, string? path, string? commit)
    {
        var schedule = new UserScheduleResponse(Guid.NewGuid(), Guid.NewGuid(), "team/type/workflow", "name", origin,
            "Etc/UTC", null, TimeSpan.FromMinutes(1), null, ScheduleOverlapPolicy.Skip, ScheduleCatchUpPolicy.Latest,
            ImmutableDictionary<string, JsonElement>.Empty, false, 1, null) { SourcePath = path, GitCommit = commit };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(schedule, UserScheduleJson.Default.UserScheduleResponse);
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(origin, document.RootElement.GetProperty("origin").GetString());
        Assert.Equal(path, document.RootElement.GetProperty("sourcePath").GetString());
        Assert.Equal(commit, document.RootElement.GetProperty("gitCommit").GetString());
        var restored = JsonSerializer.Deserialize(bytes, UserScheduleJson.Default.UserScheduleResponse)!;
        Assert.Equal(schedule.Id, restored.Id);
        Assert.Equal(path, restored.SourcePath);
        Assert.Equal(commit, restored.GitCommit);
        Assert.False(document.RootElement.TryGetProperty("content", out _));
    }
}
