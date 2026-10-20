import {
  failureLabels,
  type FailureDetail,
  type FailureSummaryData,
} from "./failures"

export function FailureSummary({
  summary,
  status,
}: {
  summary?: FailureSummaryData | null
  status?: string | null
}) {
  if (!summary)
    return status === "Failed" || status === "TimedOut" ? (
      <p className="text-xs text-muted-foreground">
        {summary === undefined
          ? "Failure details unavailable from this backend."
          : "No failure details recorded."}
      </p>
    ) : null
  const labels = failureLabels(summary)
  return (
    <div data-failure-summary className="max-w-sm min-w-0 text-xs">
      <p className="truncate font-medium" title={labels}>
        {labels}
      </p>
      <p
        className="[overflow-wrap:anywhere] text-muted-foreground"
        title={summary.primary.message}
      >
        {summary.primary.message}
      </p>
      {summary.primary.taskKey !== null && summary.primary.attempt === null ? (
        <p className="[overflow-wrap:anywhere] text-muted-foreground">
          Task: {summary.primary.taskKey} (not started)
        </p>
      ) : null}
    </div>
  )
}

export function FailureMessage({ failure }: { failure: FailureDetail }) {
  return (
    <div className="min-w-0 space-y-1 border-l-2 border-foreground/40 pl-2 text-sm [overflow-wrap:anywhere]">
      <p className="font-medium">
        {failure.type} / {failure.category} - {failure.impact}
      </p>
      <p className="whitespace-pre-wrap">{failure.message}</p>
      <p className="text-xs text-muted-foreground">
        Scheduler category: {failure.runtimeCategory ?? "Not recorded"}.
        {failure.ruleId
          ? ` Mapping: ${failure.ruleId}.`
          : " No message rule matched."}
      </p>
    </div>
  )
}
