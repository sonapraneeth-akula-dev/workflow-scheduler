import { useEffect, useId, useRef, useState, useSyncExternalStore } from "react"
import { useQuery, useQueryClient } from "@tanstack/react-query"
import { ErrorNotice } from "@/components/ErrorNotice"
import { formatTimestamp } from "@/lib/timestamps"
import { Button } from "@/components/ui/button"
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select"
import { useLive } from "@/lib/live/useLive"
import { getTimeline, type TimelineAttempt } from "./timeline-api"
import { getTaskLogs, logIdentity } from "./logs-api"
import { appendLogTail, emptyLogTail, type LogTail } from "./log-tail"

const rowHeight = 24
const viewportHeight = 480
const visibility = {
  subscribe: (listener: () => void) => {
    document.addEventListener("visibilitychange", listener)
    return () => document.removeEventListener("visibilitychange", listener)
  },
  getSnapshot: () => !document.hidden,
}
const identity = (attempt: { taskKey: string; attempt: number }) =>
  JSON.stringify([attempt.taskKey, attempt.attempt])

export function TaskLogs({ runId }: { runId: string }) {
  const id = useId()
  const [selection, setSelection] = useState<string | null>(null)
  const { snapshot } = useLive({ scope: "run", id: runId })
  const timeline = useQuery({
    queryKey: ["task-log-attempts", runId],
    queryFn: ({ signal }) => getTimeline(runId, signal),
    retry: false,
    gcTime: 0,
  })
  const delta = snapshot?.delta
  const revision = snapshot?.resyncVersion ?? 0
  const status = snapshot?.status
  const refetch = timeline.refetch
  const signal = useRef<{ delta: typeof delta; revision: number } | null>(null)
  useEffect(() => {
    if (
      status !== "live" ||
      timeline.isFetching ||
      (signal.current &&
        signal.current.delta === delta &&
        signal.current.revision === revision)
    )
      return
    signal.current = { delta, revision }
    void refetch()
  }, [delta, revision, status, timeline.isFetching, refetch])
  const error = snapshot?.error ?? timeline.error
  const selected =
    timeline.data?.attempts.find(
      (attempt) => identity(attempt) === selection
    ) ??
    timeline.data?.attempts.find(
      (attempt) => attempt.terminalStatus === null
    ) ??
    timeline.data?.attempts.at(-1)
  return (
    <section aria-label="Task logs" className="min-w-0 space-y-4">
      <h2 className="text-xl font-semibold">Task logs</h2>
      <p>
        Masked stdout, stderr and context messages, isolated by attempt.
        Archived ranges load on demand; live mirroring stops when this view is
        closed, hidden or paused.
      </p>
      <Button
        variant="outline"
        disabled={timeline.isFetching}
        onClick={() => void timeline.refetch()}
      >
        Refresh log attempts
      </Button>
      {error ? <ErrorNotice error={error} /> : null}
      {timeline.isPending ? <p role="status">Loading log attempts...</p> : null}
      {!error && timeline.data ? (
        selected ? (
          <>
            <div className="flex min-w-0 flex-col gap-2">
              <label htmlFor={id}>Log attempt</label>
              <NativeSelect
                id={id}
                className="max-w-full"
                value={identity(selected)}
                onChange={(event) => setSelection(event.target.value)}
              >
                {timeline.data.attempts.map((attempt) => (
                  <NativeSelectOption
                    key={identity(attempt)}
                    value={identity(attempt)}
                  >
                    {attempt.taskKey} - attempt {attempt.attempt} (
                    {attempt.terminalStatus ?? "In progress"})
                  </NativeSelectOption>
                ))}
              </NativeSelect>
            </div>
            <AttemptLogs
              key={identity(selected)}
              runId={runId}
              attempt={selected}
            />
          </>
        ) : (
          <p>No dispatched attempts recorded.</p>
        )
      ) : null}
    </section>
  )
}

function AttemptLogs({
  runId,
  attempt,
}: {
  runId: string
  attempt: TimelineAttempt
}) {
  const id = logIdentity(runId, attempt)
  const visible = useSyncExternalStore(
    visibility.subscribe,
    visibility.getSnapshot
  )
  const [watching, setWatching] = useState(true)
  const [follow, setFollow] = useState(attempt.terminalStatus === null)
  const [scrollTop, setScrollTop] = useState(0)
  const viewport = useRef<HTMLDivElement>(null)
  const client = useQueryClient()
  const { snapshot, retry } = useLive(
    visible && watching ? { scope: "logs", id } : undefined
  )
  const tailKey = ["task-log-tail", id]
  const tail = useQuery({
    queryKey: tailKey,
    queryFn: () => emptyLogTail,
    initialData: emptyLogTail,
    enabled: false,
    gcTime: 0,
  }).data
  const delta = snapshot?.logDelta
  const status = snapshot?.status
  const revision = snapshot?.resyncVersion ?? 0
  useEffect(() => {
    if (status === "error" || status === "reconnecting") {
      client.setQueryData<LogTail>(["task-log-tail", id], emptyLogTail)
    } else if (delta?.id === id) {
      client.setQueryData<LogTail>(["task-log-tail", id], (previous) =>
        appendLogTail(previous ?? emptyLogTail, delta)
      )
    }
  }, [client, delta, id, status])
  const offset = Math.max(
    0,
    Math.floor(scrollTop / rowHeight / 100) * 100 - 100
  )
  const archive = useQuery({
    queryKey: ["task-log-range", id, offset, revision],
    queryFn: ({ signal }) => getTaskLogs(runId, attempt, offset, 400, signal),
    retry: false,
    gcTime: 0,
    placeholderData: (previous) => previous,
  })
  const error = snapshot?.error ?? archive.error
  const page =
    !error && archive.data?.state === "ready" ? archive.data.page : undefined
  const total = Math.max(page?.totalLines ?? 0, error ? 0 : tail.lastLine)
  const first = Math.max(0, Math.floor(scrollTop / rowHeight) - 8)
  const last = Math.min(
    total,
    first + Math.ceil(viewportHeight / rowHeight) + 16
  )
  const rows = new Map([
    ...(page?.records.map((record) => [record.line, record.frame] as const) ??
      []),
    ...(!error
      ? tail.records.map((record) => [record.line, record] as const)
      : []),
  ])
  useEffect(() => {
    if (follow && viewport.current)
      viewport.current.scrollTop = Math.max(
        0,
        total * rowHeight - viewportHeight
      )
  }, [follow, total])

  function move(top: number, following: boolean) {
    setFollow(following)
    setScrollTop(top)
    if (viewport.current) viewport.current.scrollTop = top
  }

  return (
    <div className="min-w-0 space-y-3">
      <div className="flex flex-wrap gap-2">
        <Button
          variant="outline"
          onClick={() => setWatching((value) => !value)}
        >
          {watching ? "Pause live logs" : "Resume live logs"}
        </Button>
        <Button
          variant="outline"
          onClick={() =>
            move(Math.max(0, total * rowHeight - viewportHeight), true)
          }
        >
          Follow log tail
        </Button>
        <Button variant="outline" onClick={() => move(0, false)}>
          First log line
        </Button>
        <Button
          variant="outline"
          disabled={archive.isFetching}
          onClick={() => {
            retry()
            void archive.refetch()
          }}
        >
          Refresh archived logs
        </Button>
      </div>
      <p role="status">
        Live logs: {watching && visible ? (status ?? "connecting") : "paused"}.{" "}
        {total.toLocaleString()} log records.
        {archive.isFetching ? " Loading archived range..." : ""}
      </p>
      {error ? <ErrorNotice error={error} /> : null}
      {tail.gap && !error ? (
        <p role="alert">
          Live log gap detected. Refresh archived logs to recover durable
          records.
        </p>
      ) : null}
      {archive.data?.state === "uploading" && !error ? (
        <p>
          Archived logs are still uploading. Live tail begins when this attempt
          is watched.
        </p>
      ) : null}
      {page?.truncationMarker ? (
        <p role="status">{page.truncationMarker}</p>
      ) : null}
      {page?.captureInterrupted ? (
        <p role="alert">
          Log capture was interrupted; only durable records are available.
        </p>
      ) : null}
      {!error ? (
        <div
          ref={viewport}
          role="log"
          aria-label="Task log lines"
          aria-live="off"
          tabIndex={0}
          className="relative min-w-0 overflow-auto rounded border font-mono text-sm"
          style={{
            height: viewportHeight,
            contain: "strict",
            overflowAnchor: "none",
          }}
          onScroll={(event) => {
            const target = event.currentTarget
            setScrollTop(target.scrollTop)
            setFollow(
              total * rowHeight - target.scrollTop - target.clientHeight <=
                rowHeight
            )
          }}
          onKeyDown={(event) => {
            if (event.key !== "Home" && event.key !== "End") return
            event.preventDefault()
            move(
              event.key === "Home"
                ? 0
                : Math.max(0, total * rowHeight - viewportHeight),
              event.key === "End"
            )
          }}
        >
          <div
            className="relative min-w-full"
            style={{ height: total * rowHeight }}
          >
            {Array.from({ length: Math.max(0, last - first) }, (_, index) => {
              const line = first + index + 1
              const frame = rows.get(line)
              return (
                <div
                  key={line}
                  data-log-line={line}
                  className="absolute left-0 min-w-full whitespace-pre"
                  style={{
                    top: (line - 1) * rowHeight,
                    height: rowHeight,
                    lineHeight: `${rowHeight}px`,
                  }}
                >
                  <span className="inline-block w-20 pr-3 text-right text-muted-foreground">
                    {line}
                  </span>
                  {frame ? (
                    <span>
                      <time dateTime={frame.at} title={frame.at}>
                        {formatTimestamp(frame.at)}
                      </time>{" "}
                      [{frame.stream}/{frame.level}]{" "}
                      {frame.text
                        .replaceAll("\r", "\\r")
                        .replaceAll("\n", "\\n")}
                    </span>
                  ) : (
                    <span className="text-muted-foreground">
                      {archive.data?.state === "uploading"
                        ? "Not archived yet"
                        : "Loading record..."}
                    </span>
                  )}
                </div>
              )
            })}
          </div>
        </div>
      ) : (
        <p>
          Previously loaded logs are hidden until access is authorized again.
        </p>
      )}
    </div>
  )
}
