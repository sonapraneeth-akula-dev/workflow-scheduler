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

`compatibility.binpb` is the immutable initial v1 descriptor image, generated
with `buf build backend/proto -o backend/proto/compatibility.binpb`.
CI runs `buf lint`, format, and `buf breaking` against that baseline and,
when present, the pull request base schema. Do not regenerate the baseline
to hide a breaking change; introduce a new versioned package instead.
