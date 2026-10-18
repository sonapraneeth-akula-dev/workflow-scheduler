# Agent transport

`agent.proto` is the `wf.agent.v1` bidirectional agent/engine contract.
`Wf.Contracts` generates messages and client/server stubs into `obj` during
build; generated C# is not checked in. Payload JSON is UTF-8, not an embedded
CLR type or assembly. Secret references contain names, never secret values.

Each sender assigns positive journal sequence numbers. Cumulative `Ack`
means durable acceptance, not task completion. Replay retains original
sequence numbers. Ack-only messages do not trigger acknowledgements.
Control messages carry a leader epoch; an agent acknowledgement identifies
that epoch. Results are idempotent by run/node/task/attempt. A reconnecting
agent sends Hello, including in-flight attempts, before replaying results.
Missing payloads, invalid identities, unsupported protocol versions and stale
epochs must be rejected by the receiving service.

Machine reservation epochs are separate from leader epochs and stream sequence
numbers. Dispatch, task-start/completion events and resource samples retain the
assigned reservation epoch. Release sends `ReservationFence` with a strictly
newer epoch. The agent persists that fence before cancelling older work and
must stop its physical task-host work before journaling `ReservationFenced`.
Only that acknowledgement, from the current agent session and matching the
requested fence, frees the reserved machine. Disconnected machines remain
unavailable. Retried fence confirmations are idempotent; stale dispatches,
results and samples cannot revive old ownership. Legacy task events without an
epoch are accepted only for ordinary attempts originally assigned epoch 1,
while that machine still has epoch 1.

Agents advertise `supports_drain_commands`. `Drain` carries a durable UUIDv7,
deadline and current reservation epoch. The machine remains Draining across
Hello and heartbeat, and no new dispatch is sent. The agent finishes existing
work until the deadline, then cancels remaining work and waits for its physical
process trees to exit. Interrupted cancellations are settled by the engine as
AgentLost only after journaled `DrainCompleted`, not as author failures.
Completed task results precede that acknowledgement in the journal. Only the
current session's exact drain ID and machine epoch may release reservations.
Expiry or lost connectivity alone never reports Released. Released machines
remain Draining; maintenance does not automatically re-enable them.

`compatibility.binpb` is the immutable initial v1 descriptor image, generated
with `buf build backend/proto -o backend/proto/compatibility.binpb`.
CI runs `buf lint`, format, and `buf breaking` against that baseline and,
when present, the pull request base schema. Do not regenerate the baseline
to hide a breaking change; introduce a new versioned package instead.

`task_host.proto` is private agent/SDK IPC, currently readiness protocol 9.
Rebuild SDK packages when upgrading this private protocol. Every agent-created
host requires its owner-only token in `x-wf-host-token`, including readiness,
secrets and streaming RPCs. `HostReady` identifies the actual bound attempt and
machine reservation epoch. `Resume` accepts only its exact original dispatch and
returns the already-running/completed execution without invoking user code.
`RequiredSecrets` returns names for a fresh host; for an exact re-adoption it can
return original resolved values held in host memory, solely to restore masking
over authenticated private IPC. Never copy those values to durable metadata.
Additional caller-provided redaction values follow the same memory-only rule.
`Records` replays durable masked log, resource and progress records by producer
sequence. `ConfirmRecords` follows durable agent log/journal commit; its flushed
spool cursor permits deletion of only that acknowledged prefix. Resource samples
have a separate contiguous ordinal assigned by the host, whether supplied by
the agent's native sampler or by explicit offline process-only fallback.
The completed result is masked and flushed before `Execute`/`Resume` returns;
the agent then stops native sampling, stores its final observation and calls
`SealRecords`. Only then does the record stream end. Result acknowledgement
requires every producer record to be committed, including that final sample.
Hard-deadline watchdogs retain physical containment independently of the agent
and terminate descendants after the attempt deadline plus 30 seconds of grace.
