import { useState, type FormEvent } from "react"
import { useInfiniteQuery } from "@tanstack/react-query"
import { Link } from "@tanstack/react-router"
import { ErrorNotice } from "@/components/ErrorNotice"
import { StatusBadge } from "@/components/StatusBadge"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { accessDenied, runStatuses } from "@/features/catalog/api"
import { TriggerBadge } from "./TriggerBadge"
import { RunNowButton } from "@/features/series/RunNowButton"
import {
  getHistory,
  historySearchSchema,
  type HistoryCursor,
  type HistoryScope,
  type HistorySearch,
} from "./history"

export function HistoryFilters({
  search,
  onChange,
}: {
  search: HistorySearch
  onChange: (search: HistorySearch) => void
}) {
  const [error, setError] = useState<string>()
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const values: Record<string, string> = {}
    new FormData(event.currentTarget).forEach((value, key) => {
      if (typeof value === "string" && value !== "") values[key] = value
    })
    const parsed = historySearchSchema.safeParse(values)
    if (!parsed.success) {
      setError(parsed.error.issues.map((issue) => issue.message).join(" "))
      return
    }
    setError(undefined)
    onChange(parsed.data)
  }
  return (
    <form
      aria-label="History filters"
      onSubmit={submit}
      className="flex min-w-0 flex-wrap items-end gap-3"
    >
      <label className="flex flex-col gap-1">
        Run status
        <select
          name="status"
          defaultValue={search.status ?? ""}
          className="rounded border bg-background p-2"
        >
          <option value="">All statuses and fire facts</option>
          {runStatuses.map((status) => (
            <option key={status}>{status}</option>
          ))}
        </select>
      </label>
      <label className="flex min-w-0 flex-col gap-1">
        Aborted by (principal UUID)
        <input
          name="abortedBy"
          defaultValue={search.abortedBy ?? ""}
          className="min-w-0 rounded border bg-background p-2"
        />
      </label>
      <label className="flex flex-col gap-1">
        Outcome
        <select
          name="outcome"
          defaultValue={search.outcome ?? ""}
          className="rounded border bg-background p-2"
        >
          <option value="">Runs and retained fire facts</option>
          <option value="Run">Runs only</option>
          <option>Skipped</option>
          <option>Missed</option>
        </select>
      </label>
      <label className="flex flex-col gap-1">
        History order
        <select
          name="sort"
          defaultValue={search.sort ?? "createdAtDesc"}
          className="rounded border bg-background p-2"
        >
          <option value="createdAtDesc">Newest first</option>
          <option value="createdAtAsc">Oldest first</option>
        </select>
      </label>
      <Button type="submit">Filter history</Button>
      <Button type="button" variant="outline" onClick={() => onChange({})}>
        Clear history filters
      </Button>
      {error ? <p role="alert">{error}</p> : null}
    </form>
  )
}

export function HistoryTable({
  scope,
  search,
}: {
  scope: HistoryScope
  search: HistorySearch
}) {
  const query = useInfiniteQuery({
    queryKey: ["run-history", scope, search],
    initialPageParam: null as HistoryCursor,
    queryFn: ({ pageParam, signal }) =>
      getHistory(scope, search, pageParam, signal),
    getNextPageParam: (page): HistoryCursor | undefined =>
      page.nextCursor
        ? { cursor: page.nextCursor, after: page.items.at(-1)! }
        : undefined,
  })
  const rows = accessDenied(query.error)
    ? undefined
    : query.data?.pages.flatMap((page) => page.items)
  const scheduleId = query.error
    ? undefined
    : query.data?.pages.at(-1)?.currentScheduleId
  return (
    <section aria-label="Run history" className="min-w-0 space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-xl font-semibold">Runs and fire history</h2>
        <Button
          variant="outline"
          disabled={query.isFetching}
          onClick={() => void query.refetch()}
        >
          Refresh run history
        </Button>
      </div>
      {scope.seriesId && scheduleId ? (
        <RunNowButton
          key={scheduleId}
          scheduleId={scheduleId}
          seriesId={scope.seriesId}
          disabled={query.isFetching}
        />
      ) : scope.seriesId && scheduleId === null ? (
        <p className="text-sm text-muted-foreground">
          Run now unavailable: this series has no current schedule.
        </p>
      ) : null}
      <p className="text-sm text-muted-foreground">
        Times shown in UTC. Duration requires recorded start and end times.
        Variables show recorded names and types only; all values and defaults
        are omitted. Secret entries are redacted. Status and aborted-by filters
        exclude fire-only facts.
      </p>
      {query.isPending ? <p role="status">Loading run history...</p> : null}
      {query.error ? <ErrorNotice error={query.error} /> : null}
      {rows?.length === 0 ? <p role="status">No matching history.</p> : null}
      {rows && rows.length > 0 ? (
        <div
          role="region"
          aria-label="Scrollable history table"
          tabIndex={0}
          className="overflow-x-auto rounded-md border"
        >
          <table className="w-full text-left text-sm">
            <caption className="sr-only">
              Historical runs, versions, trigger, recorded variable names, fire
              reasons and committed abort attribution
            </caption>
            <thead className="border-b">
              <tr>
                {[
                  "Time (UTC) / Run",
                  "Status / Fire outcome",
                  "Duration",
                  "Trigger",
                  "Variables (names/types only)",
                  "Executed version",
                  "Committed abort",
                ].map((label) => (
                  <th scope="col" key={label} className="p-3">
                    {label}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => {
                const [team = "", type = "", wf = ""] =
                  row.workflowId.split("/")
                return (
                  <tr
                    key={`${row.kind}/${row.at}/${row.kind === "Run" ? row.runId : row.fire!.scheduleId}`}
                    className="border-b last:border-0"
                  >
                    <th scope="row" className="space-y-2 p-3 font-normal">
                      <time
                        dateTime={row.at}
                        className="block whitespace-nowrap"
                      >
                        {row.at}
                      </time>
                      {row.runId ? (
                        <Link
                          className="block font-mono"
                          to="/runs/$id"
                          params={{ id: row.runId }}
                        >
                          {row.runId}
                        </Link>
                      ) : (
                        "No run"
                      )}
                      {row.seriesId && !scope.seriesId ? (
                        <Link
                          className="block"
                          to="/series/$id"
                          params={{ id: row.seriesId }}
                        >
                          Series history
                        </Link>
                      ) : null}
                    </th>
                    <td className="space-y-2 p-3">
                      {row.status ? <StatusBadge status={row.status} /> : null}
                      {row.fire ? (
                        <>
                          <Badge variant="secondary">{row.fire.outcome}</Badge>
                          <p className="max-w-xs break-words">
                            {row.fire.reason ?? "Reason not recorded"}
                          </p>
                        </>
                      ) : null}
                    </td>
                    <td className="p-3 whitespace-nowrap">
                      {row.durationMs === null
                        ? "Not recorded"
                        : `${row.durationMs} ms`}
                    </td>
                    <td className="p-3">
                      {row.trigger ? (
                        <TriggerBadge trigger={row.trigger} />
                      ) : (
                        "No run trigger"
                      )}
                    </td>
                    <td className="p-3">
                      {row.variables.length ? (
                        <ul className="space-y-1">
                          {row.variables.map((variable) => (
                            <li key={variable.key} className="break-words">
                              {variable.key} ({variable.type})
                              {variable.redacted ? " - [redacted]" : ""}
                            </li>
                          ))}
                        </ul>
                      ) : (
                        "None recorded"
                      )}
                    </td>
                    <td className="p-3">
                      {row.versionId ? (
                        <Link
                          className="font-mono"
                          to="/t/$team/$type/w/$wf/versions/$v"
                          params={{ team, type, wf, v: row.versionId }}
                        >
                          {row.versionId}
                        </Link>
                      ) : (
                        "Not applicable"
                      )}
                    </td>
                    <td className="p-3">
                      {row.abort ? (
                        <dl className="max-w-xs space-y-1 break-words">
                          <dt className="font-medium">By</dt>
                          <dd>
                            {row.abort.byName || row.abort.by}
                            <code className="block">{row.abort.by}</code>
                          </dd>
                          <dt className="font-medium">At</dt>
                          <dd>
                            <time dateTime={row.abort.at}>{row.abort.at}</time>
                          </dd>
                          <dt className="font-medium">Reason</dt>
                          <dd>{row.abort.reason}</dd>
                          <dt className="font-medium">Source</dt>
                          <dd>{row.abort.source}</dd>
                        </dl>
                      ) : (
                        "Not recorded"
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      ) : null}
      {query.hasNextPage && rows ? (
        <Button
          variant="outline"
          disabled={query.isFetching}
          onClick={() => void query.fetchNextPage()}
        >
          {query.isFetchingNextPage
            ? "Loading more history..."
            : "Load more history"}
        </Button>
      ) : null}
    </section>
  )
}
