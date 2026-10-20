import { useQuery } from "@tanstack/react-query"
import { Link } from "@tanstack/react-router"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
import { ReportRenderer } from "@/renderers/ReportRenderer"
import { formatTimestamp } from "@/lib/timestamps"
import { getReportView } from "./api"

export function ReportView({
  runId,
  reportId,
  embedded = false,
}: {
  runId: string
  reportId: string
  embedded?: boolean
}) {
  const Heading = embedded ? "h3" : "h1"
  const result = useQuery({
    queryKey: ["report-view", runId, reportId],
    queryFn: ({ signal }) => getReportView(runId, reportId, signal),
    retry: false,
  })
  return (
    <section className="min-w-0 space-y-4" aria-label="Report viewer">
      {!embedded ? (
        <Link
          to="/runs/$id"
          params={{ id: runId }}
          className="text-primary underline"
        >
          Back to run
        </Link>
      ) : null}
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
          <Heading className="text-2xl font-semibold break-words">
            {result.data.report.title}
          </Heading>
          <p className="break-words">
            {result.data.report.kind} | {result.data.report.sourceKey} | Attempt{" "}
            {result.data.report.attempt} |{" "}
            {result.data.report.sizeBytes.toLocaleString("en-US")} bytes
          </p>
          <p className="break-words">
            Tags:{" "}
            {result.data.report.tags.length
              ? result.data.report.tags.join(", ")
              : "None"}
            {" | Published "}
            <time
              dateTime={result.data.report.createdAt}
              title={result.data.report.createdAt}
            >
              {formatTimestamp(result.data.report.createdAt)}
            </time>
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
