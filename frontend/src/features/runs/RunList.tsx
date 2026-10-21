import { useState } from "react"
import { useInfiniteQuery } from "@tanstack/react-query"
import { Link } from "@tanstack/react-router"
import { ErrorNotice } from "@/components/ErrorNotice"
import { formatTimestamp } from "@/lib/timestamps"
import { StatusBadge } from "@/components/StatusBadge"
import { Button } from "@/components/ui/button"
import { accessDenied } from "@/features/catalog/api"
import { getRuns, type RunPageCursor, type RunRow } from "./api"
import { runFilterSchema, type RunSearch } from "./search"
import { FailureSummary } from "./FailureSummary"
import { SignOffSummary } from "./SignOffSummary"

const rowHeight = 208
const overscan = 3

function groupValue(row: RunRow, filters: RunSearch): string | undefined {
  switch (filters.groupBy) {
    case "team":
      return row.team
    case "type":
      return row.type
    case "workflow":
      return row.workflowId
    case "status":
      return row.status
    case "metadata":
      return row.metadata &&
        filters.groupKey &&
        Object.hasOwn(row.metadata.values, filters.groupKey)
        ? row.metadata.values[filters.groupKey]
        : undefined
    default:
      return undefined
  }
}

function Results({ rows, filters }: { rows: RunRow[]; filters: RunSearch }) {
  const [top, setTop] = useState(0)
  const [fullList, setFullList] = useState(false)
  const grouped = filters.groupBy !== undefined && filters.groupBy !== "none"
  const ordered = grouped
    ? rows.toSorted((a, b) => {
        const x = groupValue(a, filters),
          y = groupValue(b, filters)
        return x === y
          ? 0
          : x === undefined
            ? -1
            : y === undefined
              ? 1
              : x < y
                ? -1
                : 1
      })
    : rows
  const start = fullList
    ? 0
    : Math.min(
        Math.max(0, ordered.length - 1),
        Math.max(0, Math.floor(top / rowHeight) - overscan)
      )
  const end = fullList
    ? ordered.length
    : Math.min(ordered.length, start + 4 + overscan * 2)
  return (
    <section aria-label="Run results" className="min-w-0 space-y-3">
      <p role="status">
        {rows.length} loaded runs, newest first
        {grouped ? " within each loaded group" : ""}.
      </p>
      <p className="text-sm text-muted-foreground">
        More pages may add runs to existing groups. Focus the scroll region and
        use Page Up/Down, or use the full loaded list for sequential keyboard
        and screen-reader browsing.
      </p>
      <Button variant="outline" onClick={() => setFullList((value) => !value)}>
        {fullList ? "Use virtualized list" : "Use accessible full list"}
      </Button>
      <div
        role="region"
        aria-label="Scrollable run results"
        tabIndex={0}
        className="h-[480px] max-h-[70vh] overflow-auto rounded-md border"
        onScroll={(event) => setTop(event.currentTarget.scrollTop)}
      >
        <ol
          role="list"
          aria-label="Loaded runs"
          start={start + 1}
          style={{
            paddingTop: start * rowHeight,
            paddingBottom: (ordered.length - end) * rowHeight,
          }}
        >
          {ordered.slice(start, end).map((row, index) => (
            <li
              key={row.id}
              data-run-row={row.id}
              aria-posinset={start + index + 1}
              aria-setsize={ordered.length}
              className="flex min-w-0 flex-col justify-center gap-1 border-b p-3 last:border-0"
              style={{ height: rowHeight }}
            >
              {grouped ? (
                <p
                  className="truncate text-sm font-medium"
                  title={groupValue(row, filters)}
                >
                  {filters.groupBy === "metadata"
                    ? `${filters.groupKey}: `
                    : ""}
                  {groupValue(row, filters) === undefined
                    ? "(not recorded)"
                    : groupValue(row, filters) === ""
                      ? "(empty)"
                      : groupValue(row, filters)}
                </p>
              ) : null}
              <Link
                to="/runs/$id"
                params={{ id: row.id }}
                className="block truncate font-medium"
                title={row.workflowId}
              >
                {row.workflowId}
              </Link>
              <p className="truncate font-mono text-xs" title={row.id}>
                {row.id}
              </p>
              <div className="flex min-w-0 items-center gap-2">
                <StatusBadge status={row.status} />
                <time
                  dateTime={row.createdAt}
                  title={row.createdAt}
                  className="truncate text-sm"
                >
                  {formatTimestamp(row.createdAt)}
                </time>
              </div>
              <FailureSummary
                summary={row.failureSummary}
                status={row.status}
              />
              <SignOffSummary state={row.signOff} runId={row.id} compact />
            </li>
          ))}
        </ol>
      </div>
    </section>
  )
}

export function RunList({ search }: { search: RunSearch }) {
  const filters = runFilterSchema.parse(search)
  const query = useInfiniteQuery({
    queryKey: ["runs", filters],
    initialPageParam: null as RunPageCursor,
    queryFn: ({ pageParam, signal }) => getRuns(filters, pageParam, signal),
    getNextPageParam: (page): RunPageCursor | undefined =>
      page.nextCursor
        ? { cursor: page.nextCursor, afterId: page.items.at(-1)!.id }
        : undefined,
  })
  const rows = accessDenied(query.error)
    ? undefined
    : query.data?.pages.flatMap((page) => page.items)
  return (
    <>
      <Button
        variant="outline"
        disabled={query.isFetching}
        onClick={() => void query.refetch()}
      >
        Refresh runs
      </Button>
      {query.isPending ? <p role="status">Loading runs...</p> : null}
      {query.error ? <ErrorNotice error={query.error} /> : null}
      {rows?.length === 0 ? <p role="status">No matching runs.</p> : null}
      {rows && rows.length > 0 ? (
        <Results key={JSON.stringify(search)} rows={rows} filters={search} />
      ) : null}
      {query.hasNextPage && rows ? (
        <Button
          variant="outline"
          disabled={query.isFetching}
          onClick={() => void query.fetchNextPage()}
        >
          {query.isFetchingNextPage ? "Loading more runs..." : "Load more runs"}
        </Button>
      ) : null}
    </>
  )
}
