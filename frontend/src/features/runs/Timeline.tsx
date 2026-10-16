import { memo } from "react"
import { useQuery } from "@tanstack/react-query"
import { ErrorNotice } from "@/components/ErrorNotice"
import { StatusBadge } from "@/components/StatusBadge"
import { Button } from "@/components/ui/button"
import { getTimeline, instantTicks, type TimelineAttempt } from "./timeline-api"

function orderedClock(row: TimelineAttempt) {
  return (
    instantTicks(row.dispatchedAt) >= instantTicks(row.queuedAt) &&
    (row.startedAt === null ||
      instantTicks(row.startedAt) >= instantTicks(row.dispatchedAt)) &&
    (row.endedAt === null ||
      instantTicks(row.endedAt) >=
        instantTicks(row.startedAt ?? row.dispatchedAt))
  )
}

function Chart({ attempts }: { attempts: TimelineAttempt[] }) {
  let first: bigint | undefined, last: bigint | undefined
  for (const row of attempts) {
    for (const time of [
      row.queuedAt,
      row.dispatchedAt,
      row.startedAt,
      row.endedAt,
    ]) {
      if (time === null) continue
      const at = instantTicks(time)
      if (first === undefined || at < first) first = at
      if (last === undefined || at > last) last = at
    }
  }
  const origin = first ?? 0n
  const span = (last ?? origin) - origin
  const offset = (at: string) =>
    span === 0n ? 0 : (Number(instantTicks(at) - origin) / Number(span)) * 100
  return (
    <>
      {attempts.some((row) => !orderedClock(row)) ? (
        <p role="status">
          Some recorded timestamps have differing clock order. Their chart
          intervals are omitted; the table preserves the exact recorded times
          and metrics.
        </p>
      ) : null}
      <div
        role="region"
        aria-label="Scrollable attempt chart"
        tabIndex={0}
        className="overflow-x-auto rounded-md border p-3"
      >
        <div
          role="img"
          aria-label={`Queue (Q) and execution (E) intervals for ${attempts.length} dispatched attempts. Exact times and outcomes follow in the attempt table.`}
          className="min-w-[720px]"
        >
          <div aria-hidden="true" className="space-y-3">
            <p className="text-sm">
              Q = queued/dispatched wait; E = completed execution. An active
              execution has no invented end.
            </p>
            {attempts.map((row) => {
              const queueEnd = row.startedAt ?? row.endedAt ?? row.dispatchedAt
              const ordered = orderedClock(row)
              return (
                <div
                  key={`${row.taskKey}/${row.attempt}`}
                  className="grid grid-cols-[200px_1fr] items-center gap-3 text-sm"
                >
                  <span
                    className="truncate"
                    title={`${row.taskKey} attempt ${row.attempt}`}
                  >
                    {row.taskKey} #{row.attempt}
                  </span>
                  <div className="relative h-8 rounded bg-muted/50">
                    {ordered ? (
                      <>
                        <div
                          title={`Queue wait: ${row.queueWaitMs} ms`}
                          data-timeline-queue
                          className="absolute top-0 flex h-4 min-w-[2px] items-center overflow-hidden border border-foreground/50 bg-muted text-xs"
                          style={{
                            left: `${offset(row.queuedAt)}%`,
                            width: `${offset(queueEnd) - offset(row.queuedAt)}%`,
                          }}
                        >
                          Q
                        </div>
                        {row.startedAt && row.endedAt ? (
                          <div
                            title={`Execution: ${row.durationMs} ms`}
                            data-timeline-execution
                            className="absolute bottom-0 flex h-4 min-w-[2px] items-center overflow-hidden border border-primary bg-primary text-xs text-primary-foreground"
                            style={{
                              left: `${offset(row.startedAt)}%`,
                              width: `${offset(row.endedAt) - offset(row.startedAt)}%`,
                            }}
                          >
                            E
                          </div>
                        ) : null}
                        {row.endedAt === null ? (
                          <span
                            data-timeline-active
                            className="absolute top-4 text-xs"
                            style={{
                              left: `${Math.min(75, offset(row.startedAt ?? row.dispatchedAt))}%`,
                            }}
                          >
                            {row.startedAt
                              ? "Execution active; no end"
                              : "Awaiting start; no end"}
                          </span>
                        ) : row.startedAt === null ? (
                          <span className="absolute top-4 text-xs">
                            Execution never recorded a start
                          </span>
                        ) : null}
                      </>
                    ) : (
                      <span className="text-xs">
                        Clock order differs; see exact recorded times in the
                        table.
                      </span>
                    )}
                  </div>
                </div>
              )
            })}
            <p className="text-xs text-muted-foreground">
              Relative to first recorded instant. Bar endpoints use recorded
              timestamps, not a wall-clock timer.
            </p>
          </div>
        </div>
      </div>
    </>
  )
}

function Time({ at }: { at: string | null }) {
  return at === null ? (
    <>Not recorded</>
  ) : (
    <time dateTime={at} className="whitespace-nowrap">
      {at}
    </time>
  )
}

export const Timeline = memo(function Timeline({ runId }: { runId: string }) {
  const query = useQuery({
    queryKey: ["run-timeline", runId],
    queryFn: ({ signal }) => getTimeline(runId, signal),
  })
  const timeline = query.error ? undefined : query.data
  return (
    <section aria-label="Run timeline" className="min-w-0 space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-xl font-semibold">Attempt timeline</h2>
        <Button
          variant="outline"
          disabled={query.isFetching}
          onClick={() => void query.refetch()}
        >
          Refresh timeline
        </Button>
      </div>
      <p className="text-sm text-muted-foreground">
        A committed snapshot, not a ticking duration estimate. Refresh to read
        newer attempts. Only dispatched attempts are listed; skipped, reused or
        never-dispatched tasks have no invented attempts. Unknown historical
        agents and times remain explicitly unrecorded.
      </p>
      {query.isPending ? (
        <p role="status">Loading committed timeline...</p>
      ) : null}
      {query.error ? <ErrorNotice error={query.error} /> : null}
      {timeline ? (
        <>
          <p>
            Committed sequence: {timeline.lastSeq}; run{" "}
            <StatusBadge status={timeline.status} />.
          </p>
          {timeline.attempts.length === 0 ? (
            <p role="status">No dispatched attempts recorded.</p>
          ) : (
            <>
              <Chart attempts={timeline.attempts} />
              <details open>
                <summary className="cursor-pointer font-medium">
                  Exact attempt table
                </summary>
                <div
                  role="region"
                  aria-label="Scrollable attempt table"
                  tabIndex={0}
                  className="mt-3 overflow-x-auto rounded-md border"
                >
                  <table className="w-full text-left text-sm">
                    <caption className="sr-only">
                      Recorded task attempts, agent identities, queue and
                      execution times in UTC; no values are estimated from the
                      current clock
                    </caption>
                    <thead className="border-b">
                      <tr>
                        {[
                          "Task / node",
                          "Attempt",
                          "Agent",
                          "Queued (UTC)",
                          "Dispatched (UTC)",
                          "Started (UTC)",
                          "Ended (UTC)",
                          "Queue wait",
                          "Execution duration",
                          "Outcome",
                          "Failure category",
                        ].map((label) => (
                          <th key={label} scope="col" className="p-3">
                            {label}
                          </th>
                        ))}
                      </tr>
                    </thead>
                    <tbody>
                      {timeline.attempts.map((row) => (
                        <tr
                          key={`${row.taskKey}/${row.attempt}`}
                          className="border-b last:border-0"
                        >
                          <th scope="row" className="p-3 font-normal">
                            <code>{row.taskKey}</code>
                          </th>
                          <td className="p-3">{row.attempt}</td>
                          <td className="p-3">
                            <code>{row.agentId ?? "Not recorded"}</code>
                          </td>
                          <td className="p-3">
                            <Time at={row.queuedAt} />
                          </td>
                          <td className="p-3">
                            <Time at={row.dispatchedAt} />
                          </td>
                          <td className="p-3">
                            <Time at={row.startedAt} />
                          </td>
                          <td className="p-3">
                            <Time at={row.endedAt} />
                          </td>
                          <td className="p-3 whitespace-nowrap">
                            {row.queueWaitMs} ms
                          </td>
                          <td className="p-3">
                            {row.durationMs} ms
                            {row.endedAt === null
                              ? " recorded; not completed"
                              : ""}
                          </td>
                          <td className="p-3">
                            {row.terminalStatus ? (
                              <StatusBadge status={row.terminalStatus} />
                            ) : row.startedAt ? (
                              "Active"
                            ) : (
                              "Awaiting start"
                            )}
                          </td>
                          <td className="p-3">
                            {row.failureCategory ?? "Not recorded"}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </details>
            </>
          )}
        </>
      ) : null}
    </section>
  )
})
