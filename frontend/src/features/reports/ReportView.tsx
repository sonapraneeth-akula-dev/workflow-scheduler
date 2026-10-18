import { useQuery } from "@tanstack/react-query"
import { Link } from "@tanstack/react-router"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
import { ReportRenderer } from "@/renderers/ReportRenderer"
import { getReportView } from "./api"

export function ReportView({
  runId,
  reportId,
}: {
  runId: string
  reportId: string
}) {
  const result = useQuery({
    queryKey: ["report-view", runId, reportId],
    queryFn: ({ signal }) => getReportView(runId, reportId, signal),
    retry: false,
  })
  return (
    <section className="min-w-0 space-y-4" aria-label="Report viewer">
      <Link
        to="/runs/$id"
        params={{ id: runId }}
        className="text-primary underline"
      >
        Back to run
      </Link>
      <Button
        variant="outline"
        disabled={result.isFetching}
        onClick={() => void result.refetch()}
      >
        Refresh report
      </Button>
      {result.isPending ? <p role="status">Loading report...</p> : null}
      {result.error ? <ErrorNotice error={result.error} /> : null}
      {result.data && !result.error ? (
        <>
          <h1 className="text-2xl font-semibold break-words">
            {result.data.report.title}
          </h1>
          <p className="break-words">
            {result.data.report.kind} | {result.data.report.sourceKey} | Attempt{" "}
            {result.data.report.attempt} |{" "}
            {result.data.report.sizeBytes.toLocaleString("en-US")} bytes
          </p>
          <ReportRenderer
            kind={result.data.report.kind}
            title={result.data.report.title}
            content={result.data.content}
          />
        </>
      ) : null}
    </section>
  )
}
