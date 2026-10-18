import { useEffect, useMemo, useRef, useState, type FormEvent } from "react"
import { useInfiniteQuery } from "@tanstack/react-query"
import { Link } from "@tanstack/react-router"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { runStatuses } from "@/features/catalog/api"
import { executionStatuses } from "@/lib/live/runApi"
import { useLive } from "@/lib/live/useLive"
import {
  getSystemEvents,
  systemEventsFilters,
  type SystemEvent,
  type SystemEventsFilters,
} from "./system-events-api"

const remotePhases = [
  "",
  "Submission pending",
  "Submitted",
  "Waiting",
  "Succeeded",
  "Failed",
  "Cancel requested",
  "Cancel confirmed",
  "Outcome unknown",
  "Resuming",
]

function Transition({ event }: { event: SystemEvent }) {
  return (
    <>
      {event.progress
        ? `Remote: ${remotePhases[event.progress.phase]}`
        : (event.waitingReason ??
          (event.entity === 0
            ? runStatuses[event.state]
            : executionStatuses[event.state]))}
      {event.failureCategory ? ` (${event.failureCategory})` : null}
      {event.progress ? (
        <span className="block">
          Occurred{" "}
          <time dateTime={event.progress.occurredAt}>
            {event.progress.occurredAt}
          </time>
        </span>
      ) : null}
    </>
  )
}

export function SystemEvents({ runId }: { runId: string }) {
  const [filters, setFilters] = useState<SystemEventsFilters>({})
  const [formError, setFormError] = useState<string | null>(null)
  const { snapshot, retry } = useLive({ scope: "run", id: runId })
  const queryKey = useMemo(
    () => ["system-events", runId, filters],
    [runId, filters]
  )
  const query = useInfiniteQuery({
    queryKey,
    queryFn: ({ pageParam, signal }) =>
      getSystemEvents(runId, filters, pageParam || undefined, signal),
    initialPageParam: "",
    getNextPageParam: (page) => page.nextCursor ?? undefined,
    retry: false,
    gcTime: 0,
  })
  const delta = snapshot?.delta
  const revision = snapshot?.resyncVersion ?? 0
  const status = snapshot?.status
  const refetch = query.refetch
  const signal = useRef<{ delta: typeof delta; revision: number } | null>(null)
  useEffect(() => {
    if (
      status !== "live" ||
      query.isFetching ||
      (signal.current &&
        signal.current.delta === delta &&
        signal.current.revision === revision)
    )
      return
    signal.current = { delta, revision }
    // A delta received during a read triggers a second read after it completes, never periodic polling.
    void refetch()
  }, [delta, revision, status, query.isFetching, refetch])

  function apply(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const data = new FormData(event.currentTarget)
    const values = Object.fromEntries(
      ["node", "task", "attempt"].flatMap((name) => {
        const value = data.get(name)
        return typeof value === "string" && value !== "" ? [[name, value]] : []
      })
    )
    const parsed = systemEventsFilters.safeParse(values)
    if (!parsed.success) {
      setFormError(
        "Use an exact node, full node/task key and attempt 1-32767; node and task must agree."
      )
      return
    }
    setFormError(null)
    setFilters(parsed.data)
  }

  const error = snapshot?.error ?? query.error
  const pages = error ? undefined : query.data?.pages
  const first = pages?.[0]
  const rows = pages?.flatMap((page) => page.events) ?? []
  const [team = "", type = "", wf = ""] = first?.workflowId.split("/") ?? []
  return (
    <section aria-label="Run system events" className="min-w-0 space-y-4">
      <h2 className="text-xl font-semibold">System Events</h2>
      <p>
        Read-only, ordered committed transitions, not task logs or individual
        remote polls. Planned agent means a pre-decision affinity/run pin, not a
        prediction. The pinned Task Plan declares placement constraints;
        selected agent and reason record the actual dispatch. Missing historical
        selection metadata remains unrecorded.
      </p>
      <form onSubmit={apply} className="flex flex-wrap items-end gap-3">
        <label className="min-w-0">
          Node key
          <Input name="node" placeholder="node" maxLength={512} />
        </label>
        <label className="min-w-0">
          Full task key
          <Input name="task" placeholder="node/task" maxLength={1025} />
        </label>
        <label className="min-w-0">
          Attempt
          <Input
            name="attempt"
            inputMode="numeric"
            placeholder="All attempts"
            maxLength={5}
          />
        </label>
        <Button type="submit">Apply event filters</Button>
        <Button
          type="reset"
          variant="outline"
          onClick={() => {
            setFilters({})
            setFormError(null)
          }}
        >
          Clear event filters
        </Button>
      </form>
      {formError ? <p role="alert">{formError}</p> : null}
      <div className="flex flex-wrap items-center gap-3">
        <Button
          variant="outline"
          disabled={query.isFetching}
          onClick={() => {
            retry()
            void refetch()
          }}
        >
          Refresh system events
        </Button>
        <p role="status">
          Live events: {status ?? "connecting"}
          {query.isFetching ? "; reading committed history..." : ""}
        </p>
      </div>
      {error ? <ErrorNotice error={error} /> : null}
      {first ? (
        <>
          <p>
            Committed watermark: {first.lastSeq}.{" "}
            <Link
              to="/t/$team/$type/w/$wf/versions/$v"
              params={{ team, type, wf, v: first.versionId }}
            >
              Pinned Task Plan
            </Link>
          </p>
          {rows.length === 0 ? (
            <p>No matching committed system events.</p>
          ) : (
            <div
              role="region"
              aria-label="Scrollable system event table"
              tabIndex={0}
              className="overflow-x-auto rounded-md border"
            >
              <table className="w-full text-left text-sm">
                <caption className="sr-only">
                  Committed system events in sequence order, with actual attempt
                  and agent selection history
                </caption>
                <thead>
                  <tr>
                    {[
                      "Sequence",
                      "Recorded (UTC)",
                      "Entity / key",
                      "Attempt",
                      "Transition",
                      "Planned agent (prior pin)",
                      "Selected agent",
                      "Selection reason",
                    ].map((heading) => (
                      <th scope="col" key={heading} className="p-3">
                        {heading}
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {rows.map((event) => (
                    <tr
                      key={event.seq}
                      data-system-seq={event.seq}
                      className="border-t align-top"
                    >
                      <th scope="row" className="p-3">
                        {event.seq}
                      </th>
                      <td className="p-3 whitespace-nowrap">
                        <time dateTime={event.at}>{event.at}</time>
                      </td>
                      <td className="p-3">
                        {["Run", "Node", "Task"][event.entity]}
                        {event.key ? (
                          <span className="block">{event.key}</span>
                        ) : null}
                        {event.entity === 2 && event.key ? (
                          <Link
                            to="/t/$team/$type/w/$wf/versions/$v/tasks/$taskKey"
                            params={{
                              team,
                              type,
                              wf,
                              v: first.versionId,
                              taskKey: event.key,
                            }}
                          >
                            Declared task plan
                          </Link>
                        ) : null}
                      </td>
                      <td className="p-3">{event.attempt ?? "Not recorded"}</td>
                      <td className="p-3">
                        <Transition event={event} />
                      </td>
                      <td className="p-3">
                        {event.entity === 2 && event.state === 2
                          ? (event.plannedAgentId ??
                            (event.selectionReason
                              ? "No preselected agent"
                              : "Not recorded (legacy)"))
                          : "Not an agent selection"}
                      </td>
                      <td className="p-3 break-all">
                        {event.agentId ??
                          (event.entity === 2 && event.state === 2
                            ? "Not recorded (legacy)"
                            : "-")}
                      </td>
                      <td className="p-3">{event.selectionReason ?? "-"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          {query.hasNextPage ? (
            <Button
              variant="outline"
              disabled={query.isFetching}
              onClick={() => void query.fetchNextPage()}
            >
              Load more system events
            </Button>
          ) : null}
        </>
      ) : null}
    </section>
  )
}
