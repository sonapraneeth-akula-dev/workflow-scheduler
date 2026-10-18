import { useState } from "react"
import { useInfiniteQuery, useQuery } from "@tanstack/react-query"
import { Link } from "@tanstack/react-router"
import { ErrorNotice } from "@/components/ErrorNotice"
import { formatTimestamp } from "@/lib/timestamps"
import { Button } from "@/components/ui/button"
import { getHistory, type HistoryCursor } from "@/features/runs/history"
import { ReportRenderer } from "@/renderers/ReportRenderer"
import {
  getReportComparison,
  getReportIndex,
  type ReportComparisonQuery,
} from "./api"

export function ReportCompare({ seriesId }: { seriesId: string }) {
  const [leftRunId, setLeftRunId] = useState("")
  const [rightRunId, setRightRunId] = useState("")
  const [reportId, setReportId] = useState("")
  const [selection, setSelection] = useState<ReportComparisonQuery | null>(null)
  const history = useInfiniteQuery({
    queryKey: ["run-history", { seriesId }, { outcome: "Run" }],
    initialPageParam: null as HistoryCursor,
    queryFn: ({ pageParam, signal }) =>
      getHistory({ seriesId }, { outcome: "Run" }, pageParam, signal),
    getNextPageParam: (page): HistoryCursor | undefined =>
      page.nextCursor
        ? { cursor: page.nextCursor, after: page.items.at(-1)! }
        : undefined,
    retry: false,
  })
  const index = useQuery({
    queryKey: ["reports", leftRunId],
    queryFn: ({ signal }) => getReportIndex(leftRunId, signal),
    enabled: !!leftRunId && !history.error,
    retry: false,
  })
  const runs = history.error
    ? []
    : (history.data?.pages.flatMap((page) => page.items) ?? [])
  const reports = index.error || history.error ? [] : (index.data?.items ?? [])
  const report = reports.find((item) => item.id === reportId)
  const ready =
    !!report && !!rightRunId && leftRunId !== rightRunId && !history.error
  return (
    <section className="min-w-0 space-y-4" aria-label="Report comparison">
      <Link
        to="/series/$id"
        params={{ id: seriesId }}
        className="text-primary underline"
      >
        Back to series history
      </Link>
      <h1 className="text-2xl font-semibold">Compare reports</h1>
      <p className="text-sm text-muted-foreground">
        Choose any two runs in this series, then a report from the left run.
        Matching uses its exact source key and title, not its kind or attempt.
        Missing or multiple matches cannot be compared.
      </p>
      {history.isPending ? <p role="status">Loading series runs...</p> : null}
      {history.error ? <ErrorNotice error={history.error} /> : null}
      <Button
        variant="outline"
        disabled={history.isFetching}
        onClick={() => {
          setSelection(null)
          void history.refetch()
        }}
      >
        Refresh series runs
      </Button>
      {history.hasNextPage && !history.error ? (
        <Button
          variant="outline"
          disabled={history.isFetching}
          onClick={() => void history.fetchNextPage()}
        >
          Load more series runs
        </Button>
      ) : null}
      {history.data && !history.error && !runs.length ? (
        <p role="status">No runs in this series.</p>
      ) : null}
      <form
        className="space-y-4"
        onSubmit={(event) => {
          event.preventDefault()
          if (ready && report)
            setSelection({
              leftRunId,
              rightRunId,
              sourceKey: report.sourceKey,
              title: report.title,
            })
        }}
      >
        <div className="grid min-w-0 gap-4 md:grid-cols-2">
          {(["left", "right"] as const).map((side) => (
            <label
              key={side}
              className="flex min-w-0 flex-col gap-1 overflow-clip"
            >
              {side === "left" ? "Left run" : "Right run"}
              <select
                className="h-9 w-full max-w-full min-w-0 rounded-md border bg-background px-3 focus-visible:-outline-offset-2"
                value={side === "left" ? leftRunId : rightRunId}
                disabled={!!history.error || !runs.length}
                onChange={(event) => {
                  setSelection(null)
                  if (side === "left") {
                    setLeftRunId(event.target.value)
                    setReportId("")
                  } else setRightRunId(event.target.value)
                }}
              >
                <option value="">Choose a run</option>
                {runs.map((run) => (
                  <option
                    key={run.runId}
                    value={run.runId!}
                    disabled={
                      run.runId === (side === "left" ? rightRunId : leftRunId)
                    }
                  >
                    {run.runId} | {formatTimestamp(run.at)} | {run.status}
                  </option>
                ))}
              </select>
            </label>
          ))}
        </div>
        {leftRunId && index.isPending ? (
          <p role="status">Loading left run reports...</p>
        ) : null}
        {index.error ? <ErrorNotice error={index.error} /> : null}
        <Button
          type="button"
          variant="outline"
          disabled={!leftRunId || index.isFetching || !!history.error}
          onClick={() => {
            setSelection(null)
            setReportId("")
            void index.refetch()
          }}
        >
          Refresh left run reports
        </Button>
        <label className="flex min-w-0 flex-col gap-1 overflow-clip">
          Report from left run
          <select
            className="h-9 w-full max-w-full min-w-0 rounded-md border bg-background px-3 focus-visible:-outline-offset-2"
            value={reportId}
            disabled={!reports.length}
            onChange={(event) => {
              setSelection(null)
              setReportId(event.target.value)
            }}
          >
            <option value="">Choose a report</option>
            {reports.map((item) => (
              <option key={item.id} value={item.id}>
                {item.sourceKey} | {item.title} | {item.kind} | Attempt{" "}
                {item.attempt}
              </option>
            ))}
          </select>
        </label>
        {leftRunId && index.data && !index.error && !reports.length ? (
          <p role="status">No reports published for the left run.</p>
        ) : null}
        <Button type="submit" disabled={!ready}>
          Compare selected reports
        </Button>
      </form>
      {selection && ready ? (
        <Comparison
          key={JSON.stringify(selection)}
          seriesId={seriesId}
          selection={selection}
        />
      ) : null}
    </section>
  )
}

function Comparison({
  seriesId,
  selection,
}: {
  seriesId: string
  selection: ReportComparisonQuery
}) {
  const result = useQuery({
    queryKey: ["report-comparison", seriesId, selection],
    queryFn: ({ signal }) => getReportComparison(seriesId, selection, signal),
    retry: false,
  })
  return (
    <section className="min-w-0 space-y-4" aria-label="Compared reports">
      <Button
        variant="outline"
        disabled={result.isFetching}
        onClick={() => void result.refetch()}
      >
        Refresh comparison
      </Button>
      {result.isPending ? <p role="status">Loading comparison...</p> : null}
      {result.error ? <ErrorNotice error={result.error} /> : null}
      {result.data && !result.error ? (
        <div className="grid min-w-0 gap-4 md:grid-cols-2">
          {(["left", "right"] as const).map((side) => {
            const { report, content } = result.data[side]
            return (
              <section
                key={side}
                aria-label={`${side === "left" ? "Left" : "Right"} report`}
                className="min-w-0 space-y-3 rounded-md border p-3"
              >
                <h2 className="text-xl font-semibold break-words">
                  {side === "left" ? "Left" : "Right"}: {report.title}
                </h2>
                <Link
                  to="/runs/$id"
                  params={{ id: report.runId }}
                  className="block break-all text-primary underline"
                >
                  {report.runId}
                </Link>
                <p className="break-words">
                  {report.kind} | {report.sourceKey} | Attempt {report.attempt}{" "}
                  | {report.sizeBytes.toLocaleString("en-US")} bytes
                </p>
                <ReportRenderer
                  kind={report.kind}
                  title={report.title}
                  content={content}
                />
              </section>
            )
          })}
        </div>
      ) : null}
    </section>
  )
}
