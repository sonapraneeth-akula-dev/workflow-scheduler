import { useEffect, useState } from "react"
import { useQuery, useQueryClient } from "@tanstack/react-query"
import { Tabs } from "radix-ui"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
import { NativeSelect } from "@/components/ui/native-select"
import { useLive } from "@/lib/live/useLive"
import { getReportIndex, type ReportItem } from "./api"
import { ReportView } from "./ReportView"

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
  const [selectedId, setSelectedId] = useState<string>()
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
  const ordered = groups.flatMap((group) => [...group.tasks.values()].flat())
  const selected =
    ordered.find((report) => report.id === selectedId) ?? ordered[0]
  return (
    <section className="min-w-0 space-y-4" aria-label="Run reports">
      <h2 className="text-xl font-semibold">Reports</h2>
      <div className="flex flex-wrap items-end gap-3">
        <label className="flex min-w-0 flex-col gap-1">
          Report kind
          <NativeSelect
            value={kind}
            onChange={(event) => setKind(event.target.value)}
          >
            <option value="">All kinds</option>
            {[...new Set(reports.map((report) => report.kind))].map((value) => (
              <option key={value}>{value}</option>
            ))}
          </NativeSelect>
        </label>
        <label className="flex min-w-0 flex-col gap-1">
          Report tag
          <NativeSelect
            value={tag}
            onChange={(event) => setTag(event.target.value)}
          >
            <option value="">All tags</option>
            {[...new Set(reports.flatMap((report) => report.tags))].map(
              (value) => (
                <option key={value}>{value}</option>
              )
            )}
          </NativeSelect>
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
          {selected ? (
            <Tabs.Root
              value={selected.id}
              onValueChange={setSelectedId}
              activationMode="manual"
              className="min-w-0 space-y-4"
            >
              <Tabs.List
                aria-label="Run report tabs"
                className="flex min-w-0 gap-1 overflow-x-auto border-b bg-card p-1"
              >
                {ordered.map((report) => (
                  <Tabs.Trigger
                    key={report.id}
                    value={report.id}
                    className="flex min-h-11 max-w-80 shrink-0 flex-col items-start justify-center gap-1 border-b-2 border-transparent px-3 py-2 text-left text-sm break-words text-muted-foreground hover:bg-accent focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-ring data-[state=active]:border-primary data-[state=active]:bg-secondary data-[state=active]:text-secondary-foreground"
                  >
                    <span className="font-medium">{report.title}</span>{" "}
                    <span className="text-xs">
                      {report.sourceKey} | Attempt {report.attempt}
                    </span>
                  </Tabs.Trigger>
                ))}
              </Tabs.List>
              <Tabs.Content value={selected.id} className="min-w-0">
                <ReportView
                  key={selected.id}
                  runId={runId}
                  reportId={selected.id}
                  embedded
                />
              </Tabs.Content>
            </Tabs.Root>
          ) : null}
        </>
      ) : null}
    </section>
  )
}
