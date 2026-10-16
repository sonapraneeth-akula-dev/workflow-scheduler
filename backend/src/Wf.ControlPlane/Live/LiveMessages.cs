using System.Globalization;
using MessagePack;
using Wf.Contracts.Events;

namespace Wf.ControlPlane.Live;

[MessagePackObject]
public sealed record LiveSubscriptionProblem(
    [property: Key("status")] int Status,
    [property: Key("title")] string Title,
    [property: Key("detail")] string Detail,
    [property: Key("reason")] string? Reason = null);

[MessagePackObject]
public sealed record RunDelta(
    [property: Key("runId")] string RunId,
    [property: Key("events")] LiveEvent[] Events);

[MessagePackObject]
public sealed record LiveEvent(
    [property: Key("seq")] long Seq,
    [property: Key("at")] string At,
    [property: Key("entity")] int Entity,
    [property: Key("key")] string? Key,
    [property: Key("state")] int State,
    [property: Key("abort")] LiveAbort? Abort)
{
    public static LiveEvent From(RunEvent change) => new(change.Sequence,
        change.At.ToString("O", CultureInfo.InvariantCulture), (int)change.Entity, change.Key, change.State,
        change.Abort is not { } abort ? null : new(abort.By, abort.ByName,
            abort.At.ToString("O", CultureInfo.InvariantCulture), abort.Reason, abort.Source.ToString()));
}

[MessagePackObject]
public sealed record LiveAbort(
    [property: Key("by")] string By,
    [property: Key("byName")] string ByName,
    [property: Key("at")] string At,
    [property: Key("reason")] string Reason,
    [property: Key("source")] string Source);

[MessagePackObject]
public sealed record ListDelta(
    [property: Key("scope")] string Scope,
    [property: Key("id")] string Id,
    [property: Key("items")] LiveRunSummary[] Items);

[MessagePackObject]
public sealed record LiveRunSummary(
    [property: Key("runId")] string RunId,
    [property: Key("workflowId")] string WorkflowId,
    [property: Key("versionId")] string VersionId,
    [property: Key("seriesId")] string? SeriesId,
    [property: Key("status")] int Status,
    [property: Key("lastSeq")] long LastSeq,
    [property: Key("createdAt")] string CreatedAt,
    [property: Key("endedAt")] string? EndedAt);

[MessagePackObject]
public sealed record LiveResync(
    [property: Key("scope")] string Scope,
    [property: Key("id")] string Id);

/// <summary>Run-index invalidation only; never consumes a run-event sequence.
/// A run Resync/reconnect requires refetching the report index as well as durable run state.</summary>
[MessagePackObject]
public sealed record ReportAdded(
    [property: Key("runId")] string RunId,
    [property: Key("reportId")] string ReportId);
