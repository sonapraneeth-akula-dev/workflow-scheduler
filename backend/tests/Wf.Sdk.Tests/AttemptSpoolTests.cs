using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Wf.Contracts.Agent.V1;
using Wf.Contracts.Host.V1;
using Wf.Contracts.Resources;
using Wf.Sdk.Hosting;

namespace Wf.Sdk.Tests;

public sealed class AttemptSpoolTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "wf-spool-" + Guid.NewGuid().ToString("N"));
    private readonly Dispatch dispatch = new()
    {
        Task = new() { RunId = Guid.NewGuid().ToString("D"), NodeKey = "work", TaskKey = "task", Attempt = 1 },
        ReservationEpoch = 3, Version = "1",
        DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(1))
    };

    [Fact]
    public void Records_are_durable_ordered_identity_bound_and_deleted_only_after_durable_ack()
    {
        var spool = new AttemptSpool(root, dispatch);
        spool.Log("stdout", "one");
        spool.Log("stderr", "two");
        var reopened = new AttemptSpool(root, dispatch);
        var records = reopened.ReadAfter(0).Records;
        Assert.Equal([1UL, 2UL], records.Select(record => record.Sequence));
        Assert.All(records, record => { Assert.Equal(dispatch.Task, record.Task); Assert.Equal(3UL, record.ReservationEpoch); });
        Assert.False(reopened.ReadAfter(0).Complete);
        Assert.Throws<InvalidOperationException>(reopened.RequireAcknowledged);
        Assert.Throws<InvalidDataException>(() => reopened.Confirm(3));
        Assert.Throws<InvalidDataException>(() => reopened.ReadAfter(3));
        reopened.Confirm(1);
        reopened.Confirm(1);
        Assert.Single(Directory.GetFiles(reopened.DirectoryPath, "*.record"));
        var again = new AttemptSpool(root, dispatch);
        Assert.Equal(2UL, Assert.Single(again.ReadAfter(0).Records).Sequence);
        again.Log("stdout", "three");
        Assert.Equal([2UL, 3UL], again.ReadAfter(0).Records.Select(record => record.Sequence));
        var result = new TaskCompleted { Task = dispatch.Task.Clone(), ReservationEpoch = 3, Outcome = TaskOutcome.Succeeded };
        Assert.Equal(result, again.Complete(result));
        Assert.Equal(result, TaskCompleted.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(again.DirectoryPath, "result.pb"))));
        Assert.False(again.ReadAfter(3).Complete);
        again.SealRecords();
        Assert.True(again.ReadAfter(3).Complete);
        Assert.Throws<InvalidOperationException>(again.RequireAcknowledged);
        again.Confirm(3);
        again.RequireAcknowledged();
        Assert.Empty(Directory.GetFiles(again.DirectoryPath, "*.record"));
    }

    [Fact]
    public void A_committed_result_cannot_be_overwritten_by_a_racing_deadline_guard()
    {
        var spool = new AttemptSpool(root, dispatch);
        var result = new TaskCompleted { Task = dispatch.Task.Clone(), ReservationEpoch = 3, Outcome = TaskOutcome.Succeeded };
        spool.Complete(result);
        HostDeadline.SaveTimeout(root, dispatch);
        Assert.Equal(result, TaskCompleted.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(spool.DirectoryPath, "result.pb"))));
    }

    [Fact]
    public void A_torn_or_mismatched_record_cannot_be_replayed_or_bound_to_another_attempt()
    {
        var spool = new AttemptSpool(root, dispatch);
        spool.Log("stdout", "one");
        var other = dispatch.Clone();
        other.Task.Attempt++;
        Assert.Throws<InvalidDataException>(() => new AttemptSpool(root, other));
        File.WriteAllBytes(Assert.Single(Directory.GetFiles(spool.DirectoryPath, "*.record")), [0xff]);
        Assert.Throws<InvalidProtocolBufferException>(() => new AttemptSpool(root, dispatch));
    }

    [Fact]
    public void Hard_deadline_has_exactly_thirty_seconds_of_cleanup_grace_and_a_durable_timeout_result()
    {
        var spool = new AttemptSpool(root, dispatch);
        dispatch.DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(-29));
        Assert.False(HostDeadline.Expired(dispatch));
        dispatch.DeadlineAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(-31));
        Assert.True(HostDeadline.Expired(dispatch));
        HostDeadline.SaveTimeout(root, dispatch);
        var result = TaskCompleted.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(spool.DirectoryPath, "result.pb")));
        Assert.Equal(TaskOutcome.TimedOut, result.Outcome);
        Assert.Equal("Timeout", result.Error.Category);
        Assert.Equal(dispatch.Task, result.Task);
        Assert.Equal(dispatch.ReservationEpoch, result.ReservationEpoch);
    }

    [Fact]
    public void Record_quota_and_frame_validation_are_explicit()
    {
        var spool = new AttemptSpool(root, dispatch);
        Assert.Throws<ArgumentException>(() => spool.Log("unexpected", "text"));
        Assert.Throws<ArgumentException>(() => spool.Log("stdout", new string('x', 65537)));
        var limited = new AttemptSpool(Path.Combine(root, "limited"), dispatch, maximumBytes: 100);
        Assert.Throws<IOException>(() => limited.Log("stdout", new string('x', 100)));
        Assert.Empty(limited.ReadAfter(0).Records);
        spool.Log("stdout", "safe");
        Assert.DoesNotContain("resolved_secrets", Encoding.UTF8.GetString(File.ReadAllBytes(
            Path.Combine(spool.DirectoryPath, "dispatch.pb"))), StringComparison.Ordinal);
    }

    [Fact]
    public void Native_offline_resources_and_progress_share_record_order_but_preserve_independent_sample_ordinals()
    {
        var point = new ResourcePoint(DateTimeOffset.UtcNow, null, null, 123, null, null, null, null, null, null,
            "process", "process-rss", "unavailable", "First sample.", null, "No native network counters.", "No native network rates.");
        var sample = new ResourceSample { Task = dispatch.Task.Clone(), ReservationEpoch = dispatch.ReservationEpoch, SampleSequence = 99,
            SampleJson = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(
                new ResourceObservation(point, null, "No machine counters."), ResourceJsonContext.Default.ResourceObservation)) };
        var spool = new AttemptSpool(root, dispatch);
        spool.Resource(sample);
        var first = Assert.Single(spool.ReadAfter(0).Records);
        Assert.Equal(1UL, first.Resource.SampleSequence);
        Assert.Equal(99UL, sample.SampleSequence);
        spool.Confirm(first.Sequence);
        var recovered = new AttemptSpool(root, dispatch);
        recovered.Resource(sample);
        var at = DateTimeOffset.UtcNow;
        var progress = new TaskProgress { Task = dispatch.Task.Clone(), ReservationEpoch = dispatch.ReservationEpoch,
            ProgressSequence = 1, Phase = RemoteOperationPhase.Waiting,
            At = Timestamp.FromDateTimeOffset(new DateTimeOffset(at.UtcTicks / 10 * 10, TimeSpan.Zero)) };
        recovered.Progress(progress);
        recovered.Resource(sample);
        var records = recovered.ReadAfter(0).Records;
        Assert.Equal([2UL, 3UL, 4UL], records.Select(record => record.Sequence));
        Assert.Equal(2UL, records[0].Resource.SampleSequence);
        Assert.Equal(progress, records[1].Progress);
        Assert.Equal(3UL, records[2].Resource.SampleSequence);
        var wrong = progress.Clone();
        wrong.ReservationEpoch++;
        Assert.Throws<InvalidDataException>(() => recovered.Progress(wrong));
        Assert.Throws<InvalidOperationException>(recovered.SealRecords);
        recovered.Complete(new TaskCompleted { Task = dispatch.Task.Clone(), ReservationEpoch = 3, Outcome = TaskOutcome.Succeeded });
        Assert.Throws<InvalidOperationException>(() => recovered.Log("stdout", "too late"));
        recovered.Resource(sample);
        Assert.Equal(4UL, recovered.ReadAfter(4).Records.Single().Resource.SampleSequence);
        Assert.False(recovered.ReadAfter(5).Complete);
        recovered.SealRecords();
        Assert.True(recovered.ReadAfter(5).Complete);
        Assert.Throws<InvalidOperationException>(() => recovered.Resource(sample));
        recovered.Confirm(5);
        recovered.RequireAcknowledged();
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
