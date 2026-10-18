import { useEffect, useState } from "react"
import { useQuery, useQueryClient } from "@tanstack/react-query"
import { Link } from "@tanstack/react-router"
import { ErrorNotice } from "@/components/ErrorNotice"
import { formatTimestamp } from "@/lib/timestamps"
import { Button } from "@/components/ui/button"
import { useLive } from "@/lib/live/useLive"
import { getReportIndex, type ReportItem } from "./api"

const stages = new Map([
  ["$init", "System init"],
  ["$pre", "Pre-setup"],
  ["$post", "Post-process"],
  ["$sys-post", "System post-process"],
])

export function groupReports(reports: readonly ReportItem[]) {
  const groups = new Map<
    string,
    {
      source: string
      stage: string | undefined
      tasks: Map<string, ReportItem[]>
    }
  >()
  for (const report of reports) {
    const [source, task] = report.sourceKey.split("/")
    if (!source || !task || report.sourceKey !== `${source}/${task}`)
      throw new Error("Report source is not an exact node/task key.")
    let group = groups.get(source)
    if (!group) {
      group = { source, stage: stages.get(source), tasks: new Map() }
      groups.set(source, group)
    }
    const items = group.tasks.get(task) ?? []
    items.push(report)
    group.tasks.set(task, items)
  }
  return [...groups.values()]
}

export function ReportIndex({ runId }: { runId: string }) {
  const [kind, setKind] = useState("")
  const [tag, setTag] = useState("")
  const { snapshot, retry } = useLive({ scope: "run", id: runId })
  const client = useQueryClient()
  const result = useQuery({
    queryKey: ["reports", runId],
    queryFn: ({ signal }) => getReportIndex(runId, signal),
    retry: false,
  })
  const status = snapshot?.status
  const reportVersion = snapshot?.reportVersion ?? 0
  const resync = snapshot?.resyncVersion ?? 0
  useEffect(() => {
    if (status !== "live") return
    // Restart even an initial in-flight read: its snapshot can precede the event.
    void client
      .cancelQueries({ queryKey: ["reports", runId], exact: true })
      .then(() =>
        client.invalidateQueries({ queryKey: ["reports", runId], exact: true })
      )
  }, [client, runId, status, reportVersion, resync])

  const error = snapshot?.error ?? result.error
  const reports = error ? [] : (result.data?.items ?? [])
  const filtered = reports.filter(
    (report) =>
      (!kind || report.kind === kind) && (!tag || report.tags.includes(tag))
  )
  const groups = groupReports(filtered)
  return (
    <section className="min-w-0 space-y-4" aria-label="Run reports">
      <h2 className="text-xl font-semibold">Reports</h2>
      <div className="flex flex-wrap items-end gap-3">
        <label className="flex min-w-0 flex-col gap-1">
          Report kind
          <select
            value={kind}
            onChange={(event) => setKind(event.target.value)}
            className="h-9 max-w-full rounded-md border bg-background px-3"
          >
            <option value="">All kinds</option>
            {[...new Set(reports.map((report) => report.kind))].map((value) => (
              <option key={value}>{value}</option>
            ))}
          </select>
        </label>
        <label className="flex min-w-0 flex-col gap-1">
          Report tag
          <select
            value={tag}
            onChange={(event) => setTag(event.target.value)}
            className="h-9 max-w-full rounded-md border bg-background px-3"
          >
            <option value="">All tags</option>
            {[...new Set(reports.flatMap((report) => report.tags))].map(
              (value) => (
                <option key={value}>{value}</option>
              )
            )}
          </select>
        </label>
        <Button
          variant="outline"
          onClick={() => {
            setKind("")
            setTag("")
          }}
        >
          Clear report filters
        </Button>
        <Button
          variant="outline"
          disabled={result.isFetching}
          onClick={() => void result.refetch()}
        >
          Refresh reports
        </Button>
      </div>
      <p className="text-sm text-muted-foreground">
        Exact kind and tag filters combine with AND over the committed index.
      </p>
      {status !== "live" ? (
        <p role="status">
          Live report updates {status ?? "connecting"}. Refresh reads the
          committed index.
        </p>
      ) : (
        <p className="text-sm text-muted-foreground">Report updates live.</p>
      )}
      {snapshot?.error ? (
        <Button variant="outline" onClick={retry}>
          Retry report updates
        </Button>
      ) : null}
      {error ? <ErrorNotice error={error} /> : null}
      {result.isPending ? <p role="status">Loading report index...</p> : null}
      {result.data && !error ? (
        <>
          <p role="status" aria-live="polite">
            {filtered.length} of {reports.length} reports
          </p>
          {!reports.length ? <p>No reports published for this run.</p> : null}
          {reports.length > 0 && !filtered.length ? (
            <p>No reports match these filters.</p>
          ) : null}
          {groups.map((group) => (
            <section key={group.source} className="space-y-3">
              <h3 className="text-lg font-semibold break-words">
                {group.stage
                  ? `Stage ${group.stage} (${group.source})`
                  : `Node ${group.source}`}
              </h3>
              {[...group.tasks].map(([task, items]) => (
                <section key={task} className="space-y-2">
                  <h4 className="font-semibold break-words">Task {task}</h4>
                  <ul className="space-y-2">
                    {items.map((report) => (
                      <li
                        key={report.id}
                        className="space-y-1 rounded border p-3 break-words"
                      >
                        <Link
                          className="text-primary underline"
                          to="/runs/$id/reports/$reportId"
                          params={{ id: runId, reportId: report.id }}
                        >
                          {report.title}
                        </Link>
                        <p>
                          {report.kind} | {report.sourceKey} | Attempt{" "}
                          {report.attempt} |{" "}
                          {report.sizeBytes.toLocaleString("en-US")} bytes
                        </p>
                        <p>
                          Tags:{" "}
                          {report.tags.length ? report.tags.join(", ") : "None"}
                        </p>
                        <p>
                          Published{" "}
                          <time
                            dateTime={report.createdAt}
                            title={report.createdAt}
                          >
                            {formatTimestamp(report.createdAt)}
                          </time>
                        </p>
                      </li>
                    ))}
                  </ul>
                </section>
              ))}
            </section>
          ))}
        </>
      ) : null}
    </section>
  )
}
