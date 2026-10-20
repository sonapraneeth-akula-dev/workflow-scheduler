import { useState } from "react"
import { useQuery } from "@tanstack/react-query"
import { Button } from "@/components/ui/button"
import { ErrorNotice } from "@/components/ErrorNotice"
import type { RunSnapshot } from "@/lib/live/runApi"
import { FailureMessage, FailureSummary } from "./FailureSummary"
import { completedTimelineQuery } from "./timeline-query"

export function RunFailures({ run }: { run: RunSnapshot }) {
  const [open, setOpen] = useState(false)
  const query = useQuery(completedTimelineQuery(run))
  const data = query.isSuccess && !query.isFetching ? query.data : undefined
  return (
    <section
      aria-label="Failure diagnostics"
      className="min-w-0 space-y-2 border bg-card p-3"
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="font-semibold">Failure diagnostics</h2>
        <Button
          variant="outline"
          size="sm"
          disabled={query.isFetching}
          onClick={() => void query.refetch()}
        >
          Refresh failures
        </Button>
      </div>
      {query.isFetching ? (
        <p role="status">Loading recorded failures...</p>
      ) : null}
      {query.error ? <ErrorNotice error={query.error} /> : null}
      {!query.error && !query.isFetching && !data ? (
        <p className="text-sm text-muted-foreground">
          Loads after completion. Refresh to inspect committed failures while
          the run continues.
        </p>
      ) : null}
      {data?.failures === undefined && data ? (
        <p>Failure details unavailable from this backend.</p>
      ) : null}
      {data?.failures?.length === 0 ? (
        <p>No recorded execution failures.</p>
      ) : null}
      {data?.failures?.length ? (
        <>
          <FailureSummary summary={data.failureSummary} />
          <details onToggle={(event) => setOpen(event.currentTarget.open)}>
            <summary className="cursor-pointer text-sm font-medium">
              All recorded failures ({data.failures.length};{" "}
              {data.failureSummary?.recoveredCount ?? 0} recovered)
            </summary>
            {open ? (
              <ol className="mt-3 space-y-3">
                {data.failures.map((failure) => (
                  <li
                    key={JSON.stringify([
                      failure.nodeKey,
                      failure.taskKey,
                      failure.attempt,
                    ])}
                  >
                    <p className="mb-1 text-xs font-medium [overflow-wrap:anywhere]">
                      {failure.taskKey ?? failure.nodeKey}
                      {failure.attempt === null
                        ? failure.taskKey === null
                          ? " - node failure"
                          : " - not started"
                        : ` - attempt ${failure.attempt}`}
                    </p>
                    <FailureMessage failure={failure} />
                  </li>
                ))}
              </ol>
            ) : null}
          </details>
        </>
      ) : null}
    </section>
  )
}
