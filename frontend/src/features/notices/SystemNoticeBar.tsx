import { useEffect, useState } from "react"
import { Button } from "@/components/ui/button"
import { formatTimestamp } from "@/lib/timestamps"
import type { SystemNotice } from "./api"
import { useSystemNotice } from "./useSystemNotice"

export function SystemNoticeBar() {
  const query = useSystemNotice()
  const [now, setNow] = useState(Date.now)
  const notice = query.data?.notice
  const expiresAt = notice?.expiresAt ? Date.parse(notice.expiresAt) : null
  const staleAt = query.dataUpdatedAt ? query.dataUpdatedAt + 90_000 : null
  useEffect(() => {
    const next = [expiresAt, staleAt].filter(
      (value): value is number => value !== null && value > now
    )
    if (!next.length) return
    const timer = window.setTimeout(
      () => setNow(Date.now()),
      Math.min(Math.min(...next) - Date.now() + 1, 2_147_483_647)
    )
    return () => window.clearTimeout(timer)
  }, [expiresAt, staleAt, now])
  const visible = notice && (expiresAt === null || expiresAt > now)
  const stale = staleAt !== null && now >= staleAt
  return (
    <>
      <div
        role="status"
        aria-atomic="true"
        className={
          visible && notice.severity !== "Critical"
            ? "border-b bg-card px-5 py-3 sm:px-8"
            : "sr-only"
        }
      >
        {visible && notice.severity !== "Critical" ? (
          <NoticeText notice={notice} />
        ) : null}
      </div>
      <div
        role="alert"
        aria-atomic="true"
        className={
          visible && notice.severity === "Critical"
            ? "border-b bg-card px-5 py-3 sm:px-8"
            : "sr-only"
        }
      >
        {visible && notice.severity === "Critical" ? (
          <NoticeText notice={notice} />
        ) : null}
      </div>
      {query.error || query.isPaused || stale ? (
        <div
          role="status"
          className="flex flex-wrap items-center gap-2 border-b bg-card px-5 py-2 text-sm sm:px-8"
        >
          <span>
            {query.isPaused
              ? "System notice updates are offline."
              : "System notices could not be refreshed."}
            {visible
              ? " Showing the last received notice until its expiry."
              : " Current notice status is unknown."}{" "}
            {query.error?.message}
          </span>
          <Button
            variant="outline"
            size="sm"
            disabled={query.isFetching}
            onClick={() => void query.refetch()}
          >
            Retry notices
          </Button>
        </div>
      ) : null}
    </>
  )
}

function NoticeText({ notice }: { notice: SystemNotice }) {
  return (
    <div className="min-w-0 space-y-1 [overflow-wrap:anywhere]">
      <p
        className={`font-semibold ${notice.severity === "Critical" ? "text-destructive" : notice.severity === "Warning" ? "text-warning" : "text-primary"}`}
      >
        {notice.severity === "Info" ? "Information" : notice.severity} system
        notice
      </p>
      <p className="text-sm whitespace-pre-wrap">{notice.message}</p>
      {notice.expiresAt ? (
        <p className="text-xs text-muted-foreground">
          Until{" "}
          <time dateTime={notice.expiresAt}>
            {formatTimestamp(notice.expiresAt)}
          </time>
        </p>
      ) : null}
    </div>
  )
}
