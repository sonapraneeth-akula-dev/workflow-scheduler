# Decision: Accept the measured warm-host footprint variance temporarily

- **Date:** October 16, 2026
- **Status:** Accepted; optimization deferred for later review
- **Task:** 2.2.2.2.3
- **Decision authority:** Explicit user direction to proceed without treating this variance as a blocker

## Decision

Accept the observed Windows cold per-task harness footprint as a non-blocking
variance. Task 2.2.2.2.3 is complete with this documented exception. Retain the
original 50,000,000-byte optimization target; this decision does not claim that
the target was met or introduce a new permanent cap.

The four-process/200,000,000-byte sampled-RSS LRU pool and exact five-minute
idle eviction remain implemented and unchanged. No runtime behavior, benchmark
arithmetic, collection policy or admission limit is changed by this decision.

## Recorded evidence

Final measurements include cold agent growth plus the actual task-host RSS for
the same package, without subtracting runtime memory or forcing a working-set
purge.

| Configuration | Measured bytes/task | Decimal MB/task | Above the 50 MB target |
|---|---:|---:|---:|
| MaxTasks = 1 | 53,555,200 | 53.5552 | 3.5552 MB / 7.11% |
| MaxTasks = 2 | 51,558,400 | 51.5584 | 1.5584 MB / 3.12% |

Previously verified idle footprint is approximately 26 MB RSS and 0.0244%
normalized CPU over 20 seconds, below the separate 100 MB / 1% idle limits.
Functional LRU, eviction, startup/shutdown and package-pin tests passed.
Linux footprint was not measured locally; no cross-platform or remote CI
performance result is implied.

The existing strict opt-in 50 MB benchmark remains unchanged and continues to
identify the target miss. Under this decision, that known result does not block
task completion or unrelated implementation. No measurements were rerun for
this documentation-only acceptance.

## Revisit later

Reproduce the same cold combined-memory measurement before changing limits or
claiming the original target is met. Review both one-task and two-task cases,
Windows/Linux behavior, and projected aggregate memory at the intended machine
concurrency. Prefer a real allocation reduction; do not hide runtime overhead
through subtraction, artificial collection or working-set trimming.

This acceptance covers only the recorded footprint variance. It does not waive
functional correctness, the global warm-pool bounds, idle eviction, or other
performance and recovery requirements.
