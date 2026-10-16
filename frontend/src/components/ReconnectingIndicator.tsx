import { Button } from "@/components/ui/button"
import type { RunStoreSnapshot } from "@/lib/live/runStore"

export function ReconnectingIndicator({
  status,
  error,
  retry,
}: RunStoreSnapshot & { retry: () => void }) {
  if (status === "live") return null
  return (
    <div
      role={status === "error" ? "alert" : "status"}
      className="flex flex-wrap items-center gap-3"
    >
      <p>
        {status === "error"
          ? (error?.message ?? "Run updates failed.")
          : status === "loading"
            ? "Loading authoritative run state..."
            : "Reconnecting and restoring run updates..."}
      </p>
      {status === "error" ? (
        <Button variant="outline" onClick={retry}>
          Retry run updates
        </Button>
      ) : null}
    </div>
  )
}
