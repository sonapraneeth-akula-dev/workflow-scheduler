import { useId, useState, type FormEvent } from "react"
import { useInfiniteQuery } from "@tanstack/react-query"
import { Link } from "@tanstack/react-router"
import { ErrorNotice } from "@/components/ErrorNotice"
import { formatTimestamp } from "@/lib/timestamps"
import { StatusBadge } from "@/components/StatusBadge"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { NativeSelect } from "@/components/ui/native-select"
import { Field, FieldGroup, FieldLabel } from "@/components/ui/field"
import { Input } from "@/components/ui/input"
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
  const id = useId()
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
      className="flex min-w-0 flex-col gap-3"
    >
      <FieldGroup className="grid min-w-0 gap-3 sm:grid-cols-2 xl:grid-cols-[repeat(4,minmax(0,1fr))_auto]">
        <Field className="min-w-0">
          <FieldLabel htmlFor={`${id}-status`} className="whitespace-nowrap">
            Run status
          </FieldLabel>
          <NativeSelect
            id={`${id}-status`}
            name="status"
            className="w-full"
            defaultValue={search.status ?? ""}
          >
            <option value="">All statuses and schedule events</option>
            {runStatuses.map((status) => (
              <option key={status}>{status}</option>
            ))}
          </NativeSelect>
        </Field>
        <Field className="min-w-0" data-invalid={error !== undefined}>
          <FieldLabel htmlFor={`${id}-abortedBy`} className="whitespace-nowrap">
            Cancelled by (principal UUID)
          </FieldLabel>
          <Input
            id={`${id}-abortedBy`}
            name="abortedBy"
            defaultValue={search.abortedBy ?? ""}
            aria-invalid={error !== undefined}
            aria-describedby={error ? `${id}-error` : undefined}
            className="h-9"
          />
        </Field>
        <Field className="min-w-0">
          <FieldLabel htmlFor={`${id}-outcome`} className="whitespace-nowrap">
            Outcome
          </FieldLabel>
          <NativeSelect
            id={`${id}-outcome`}
            name="outcome"
            className="w-full"
            defaultValue={search.outcome ?? ""}
          >
            <option value="">Runs and schedule events</option>
            <option value="Run">Runs only</option>
            <option>Skipped</option>
            <option>Missed</option>
          </NativeSelect>
        </Field>
        <Field className="min-w-0">
          <FieldLabel htmlFor={`${id}-sort`} className="whitespace-nowrap">
            History order
          </FieldLabel>
          <NativeSelect
            id={`${id}-sort`}
            name="sort"
            className="w-full"
            defaultValue={search.sort ?? "createdAtDesc"}
          >
            <option value="createdAtDesc">Newest first</option>
            <option value="createdAtAsc">Oldest first</option>
          </NativeSelect>
        </Field>
        <div className="flex flex-wrap items-center gap-2 self-end sm:col-span-2 xl:col-span-1">
          <Button type="submit" className="h-9">
            Filter history
          </Button>
          <Button
            type="button"
            variant="outline"
            className="h-9"
            onClick={() => onChange({})}
          >
            Clear history filters
          </Button>
        </div>
      </FieldGroup>
      {error ? (
        <p id={`${id}-error`} role="alert">
          {error}
        </p>
      ) : null}
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
  const hasCancellation = rows?.some(
    (row) => row.abort || row.status === "Aborting" || row.status === "Aborted"
  )
  const scheduleId = query.error
    ? undefined
    : query.data?.pages.at(-1)?.currentScheduleId
  return (
    <section aria-label="Run history" className="min-w-0 space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-xl font-semibold">Run history</h2>
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
        Times shown in India Standard Time (IST, UTC+05:30) with milliseconds.
        Duration requires recorded start and end times. Schedule triggered means
        a run was created, not that it is still running. Status and cancelled-by
        filters exclude schedule events without a run.
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
              Historical runs, versions, trigger, scheduling reasons and
              cancellation audit
            </caption>
            <thead className="border-b">
              <tr>
                {[
                  "Date / time (IST)",
                  "Run",
                  "Status / Scheduling",
                  "Duration",
                  "Trigger",
                  "Executed version",
                  ...(hasCancellation ? ["Cancellation"] : []),
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
                const reason = row.fire?.reason
                const scheduleReason =
                  reason === "overlap"
                    ? "Another run was active (overlap policy)."
                    : reason === "missed-grace"
                      ? "Scheduled time exceeded the scheduler's grace period."
                      : reason === "catch-up"
                        ? "Recovery run created for the latest missed schedule (catch-up)."
                        : reason ||
                          (row.fire?.outcome === "Skipped" ||
                          row.fire?.outcome === "Missed"
                            ? "Scheduling reason not recorded."
                            : null)
                return (
                  <tr
                    key={`${row.kind}/${row.at}/${row.kind === "Run" ? row.runId : row.fire!.scheduleId}`}
                    className="border-b last:border-0"
                  >
                    <td className="p-3 whitespace-nowrap">
                      <time dateTime={row.at} title={row.at}>
                        {formatTimestamp(row.at, "Asia/Kolkata")}
                      </time>
                    </td>
                    <th scope="row" className="space-y-2 p-3 font-normal">
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
                      <div className="flex flex-wrap items-center gap-2">
                        {row.status ? (
                          <StatusBadge status={row.status} />
                        ) : null}
                        {row.fire ? (
                          <Badge
                            variant="secondary"
                            title={
                              row.fire.outcome === "Started"
                                ? "The schedule created this run. Its current result is shown by the run status."
                                : undefined
                            }
                          >
                            {
                              {
                                Started: "Schedule triggered",
                                Skipped: "Schedule skipped",
                                Missed: "Schedule missed",
                                CaughtUp: "Recovery triggered",
                              }[row.fire.outcome]
                            }
                          </Badge>
                        ) : null}
                      </div>
                      {scheduleReason ? (
                        <p className="max-w-xs break-words">{scheduleReason}</p>
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
                      {row.versionId ? (
                        <Link
                          className="font-mono"
                          title={`Immutable version ID: ${row.versionId}`}
                          to="/t/$team/$type/w/$wf/versions/$v"
                          params={{ team, type, wf, v: row.versionId }}
                        >
                          {row.version ?? "Version not recorded"}
                        </Link>
                      ) : (
                        "Not applicable"
                      )}
                    </td>
                    {hasCancellation ? (
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
                              <time
                                dateTime={row.abort.at}
                                title={row.abort.at}
                              >
                                {formatTimestamp(row.abort.at, "Asia/Kolkata")}
                              </time>
                            </dd>
                            <dt className="font-medium">Reason</dt>
                            <dd className="whitespace-pre-wrap">
                              {row.abort.reason}
                            </dd>
                            <dt className="font-medium">Source</dt>
                            <dd>{row.abort.source}</dd>
                          </dl>
                        ) : row.status === "Aborted" ||
                          row.status === "Aborting" ? (
                          "Cancellation details not recorded"
                        ) : row.kind === "Fire" ? (
                          "Not applicable"
                        ) : (
                          "Not cancelled"
                        )}
                      </td>
                    ) : null}
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
