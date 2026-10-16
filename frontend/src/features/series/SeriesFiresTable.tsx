import { useInfiniteQuery } from "@tanstack/react-query"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Skeleton } from "@/components/ui/skeleton"
import { ErrorNotice } from "@/components/ErrorNotice"
import { formatFire } from "@/features/schedules/schedule-draft"
import { TriggerBadge } from "@/features/runs/TriggerBadge"
import { getSeriesFires } from "@/lib/api/client"

export function SeriesFiresTable({ seriesId }: { seriesId: string }) {
  const query = useInfiniteQuery({
    queryKey: ["series-fires", seriesId],
    initialPageParam: "",
    queryFn: ({ pageParam, signal }) =>
      getSeriesFires(seriesId, pageParam || undefined, signal),
    getNextPageParam: (page) => page.nextCursor ?? undefined,
  })
  const fires = query.data?.pages.flatMap((page) => page.items)
  return (
    <section
      aria-label="Scheduled fire history"
      className="flex min-w-0 flex-col gap-4"
    >
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-xl font-semibold">Scheduled fires</h2>
        <Button
          variant="outline"
          disabled={query.isFetching}
          onClick={() => void query.refetch()}
        >
          Refresh history
        </Button>
      </div>
      <p>
        Newest first. Includes skipped and missed fires, even when no run was
        created. Times shown in UTC.
      </p>
      {query.isPending ? (
        <div role="status" aria-label="Loading fire history">
          <Skeleton className="h-20 w-full" />
        </div>
      ) : null}
      {query.error ? <ErrorNotice error={query.error} /> : null}
      {fires?.length === 0 ? <p role="status">No recorded fires.</p> : null}
      {fires && fires.length > 0 ? (
        <div
          className="overflow-x-auto rounded-md border"
          role="region"
          aria-label="Fire history table"
          tabIndex={0}
        >
          <table className="w-full text-left text-sm">
            <caption className="sr-only">
              Scheduled fire outcomes, reasons and associated runs
            </caption>
            <thead className="border-b">
              <tr>
                <th scope="col" className="p-3">
                  Scheduled time (UTC)
                </th>
                <th scope="col" className="p-3">
                  Outcome
                </th>
                <th scope="col" className="p-3">
                  Reason
                </th>
                <th scope="col" className="p-3">
                  Run
                </th>
              </tr>
            </thead>
            <tbody>
              {fires.map((fire) => (
                <tr key={fire.fireAt} className="border-b last:border-0">
                  <th scope="row" className="p-3 font-normal whitespace-nowrap">
                    <time dateTime={fire.fireAt} title={fire.fireAt}>
                      {formatFire(fire.fireAt, "Etc/UTC")}
                    </time>
                  </th>
                  <td className="p-3">
                    <Badge variant="secondary">{fire.outcome}</Badge>
                  </td>
                  <td className="max-w-xs p-3 break-words">
                    {fire.reason ?? "None"}
                  </td>
                  <td className="p-3">
                    {fire.runId ? (
                      <div className="flex flex-col items-start gap-2">
                        <code>{fire.runId}</code>
                        {fire.reason === "catch-up" ? (
                          <TriggerBadge trigger="CatchUp" />
                        ) : null}
                      </div>
                    ) : (
                      "No run"
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}
      {query.hasNextPage ? (
        <Button
          variant="outline"
          disabled={query.isFetching}
          onClick={() => void query.fetchNextPage()}
        >
          {query.isFetchingNextPage
            ? "Loading older fires..."
            : "Load older fires"}
        </Button>
      ) : null}
    </section>
  )
}
