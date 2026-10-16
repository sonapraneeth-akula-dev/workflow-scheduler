# Decision: Separate agent reclaim, replacement start and task completion

- **Date:** October 16, 2026
- **Status:** Accepted; real wall-clock verification in progress
- **Task:** 2.3.2.1.2; corresponding US-020 and US-021 timing criteria
- **Decision authority:** Explicit user approval to proceed with the recommended policy and remove the conflicting acceptance blocker

## Blocker

The approved policy preserves running attempts until 30 seconds after the
agent's last heartbeat. At 15 seconds the agent becomes Unhealthy and receives
no new dispatches, but existing attempts remain reclaimable until 30 seconds.
Only after that deadline may an unreachable agent's attempts become
`AgentLost` and enter platform replacement.

The previous task acceptance required the replacement to execute successfully
within those same 30 seconds. That left no time for loss classification,
durable writes, placement, dispatch, task-host startup or execution. The
one-second sweeper cadence also introduces positive scheduling delay. This was
an inconsistent acceptance condition, not evidence that functional recovery
was missing.

Reclaim grace protects surviving task hosts during temporary disconnection,
agent restart and re-adoption. Starting a replacement early can overlap the
original execution. Database fencing rejects stale results; it does not undo
external side effects already performed by the old attempt.

## Accepted policy

Keep the full 30-second absolute reclaim deadline from the last heartbeat.
Do not shorten it, add a new grace after leader takeover, or begin an
unreachable agent's replacement before expiry. The existing immediate
classification of attempts omitted from a reconnecting agent's `Hello.inflight`
remains unchanged.

Measure three distinct phases:

| Phase | Acceptance |
|---|---|
| Loss classification | Attempts remain reclaimable before expiry; classify on the next successful hosted sweep after expiry. Record last-heartbeat, expiry and classification timestamps. |
| Replacement start | With a healthy database, active leader and already available eligible spare capacity, the replacement actually starts within an additional 30 seconds after reclaim expiry. This budget includes classification, durable replacement, dispatch and host startup; it is not a second reclaim grace. |
| Task completion | The replacement succeeds or reports its actual terminal outcome within its configured attempt, node and run deadlines. Work duration is not hidden inside the recovery-start budget. |

The additional 30-second replacement-start gate is the implementation's bounded
engineering acceptance for this decision, not a measured latency or a claim of
30-second total recovery. Under the stated capacity/health conditions, its
maximum start target is 60 seconds from the last heartbeat. Record measured
phase timings separately; do not report the ceiling as an observation.

When no eligible machine is available, preserve explicit placement waiting and
the existing deadlines rather than claim a capacity-independent start SLA.
Existing retry limits, author-retry accounting, `.NoAutoRetry()`, fencing and
deadline enforcement remain unchanged.

## Verification and scope

Existing functional evidence includes 39 affected passing cases and an actual
agent killed mid-SDK delegate, followed by successful execution on another
actual agent after virtual-clock reclaim expiry. Virtual-clock evidence
establishes boundaries and behavior, not real elapsed-time performance.

A separate real wall-clock acceptance test is being added: kill the executing
agent, retain the actual heartbeat timestamp, keep an eligible second agent
available, run the hosted sweeper and replacement handler, and record the
replacement's actual execution start and successful completion. No test clock
advance or early loss injection may substitute for the full reclaim interval.
Task completion will be recorded after that verification, not merely because
the contradictory requirement was removed.

This decision resolves the agent-loss timing blocker. It does not waive
functional correctness or claim host survival/re-adoption, checkpoint recovery,
the full chaos suite, Linux performance or remote CI execution. Engine-leader
failover and database-failover NFR-033 targets are unchanged.
