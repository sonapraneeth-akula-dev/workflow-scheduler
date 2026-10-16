import { Link } from "@tanstack/react-router"
import { useQuery } from "@tanstack/react-query"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
import { ApiError } from "@/lib/api/client"
import { getReservationQueue } from "./reservation-api"

export function QueuedBanner({
  runId,
  active,
}: {
  runId: string
  active: boolean
}) {
  const queue = useQuery({
    queryKey: ["reservation-queue", runId],
    queryFn: ({ signal }) => getReservationQueue(runId, signal),
    enabled: active,
    retry: false,
    gcTime: 0,
    refetchInterval: (query) =>
      query.state.error instanceof ApiError &&
      [401, 403].includes(query.state.error.status)
        ? false
        : 2_000,
  })
  if (!active) return null
  if (queue.error)
    return (
      <section aria-label="Reservation queue" className="space-y-2">
        <ErrorNotice error={queue.error} />
        <Button
          variant="outline"
          disabled={queue.isFetching}
          onClick={() => void queue.refetch()}
        >
          Retry reservation queue
        </Button>
      </section>
    )
  if (!queue.data?.items.length) return null
  return (
    <section
      aria-label="Reservation queue"
      aria-live="polite"
      aria-atomic="true"
      className="min-w-0 space-y-2 rounded border bg-muted p-4 break-words"
    >
      <h2 className="font-semibold">Waiting for reserved machines</h2>
      {queue.data.items.map((item) => (
        <div key={item.nodeKey} className="space-y-1">
          <p>
            Node {item.nodeKey}: queue position {item.position}. Needs{" "}
            {item.count} {item.count === 1 ? "machine" : "machines"}.
          </p>
          {item.blockingRunId ? (
            <p>
              Blocked by run{" "}
              <Link
                to="/runs/$id"
                params={{ id: item.blockingRunId }}
                className="break-all underline"
              >
                {item.blockingRunId}
              </Link>{" "}
              (reservation{" "}
              <span className="break-all">{item.blockingReservationId}</span>).
            </p>
          ) : (
            <p>
              Waiting for matching free machines or earlier queued requests.
            </p>
          )}
        </div>
      ))}
      <p className="text-sm">Queue details refresh every 2 seconds.</p>
    </section>
  )
}
