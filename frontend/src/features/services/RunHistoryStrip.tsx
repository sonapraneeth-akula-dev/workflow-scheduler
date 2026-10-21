import { Link } from "@tanstack/react-router"
import {
  AlarmClock,
  Ban,
  CircleCheck,
  CircleX,
  Clock,
  OctagonPause,
  Play,
} from "lucide-react"
import { formatTimestamp } from "@/lib/timestamps"
import type { ServiceWorkflow } from "./api"
import { useState } from "react"
import { failureLabels } from "@/features/runs/failures"
import { FailureSummary } from "@/features/runs/FailureSummary"
import {
  SignOffSummary,
  signOffDescription,
} from "@/features/runs/SignOffSummary"

const outcomes = {
  Queued: { icon: Clock, color: "bg-info" },
  Running: { icon: Play, color: "bg-info" },
  Aborting: { icon: OctagonPause, color: "bg-info" },
  Succeeded: { icon: CircleCheck, color: "bg-success" },
  Failed: { icon: CircleX, color: "bg-destructive" },
  TimedOut: { icon: AlarmClock, color: "bg-chart-4" },
  Aborted: { icon: Ban, color: "bg-warning" },
}

export function RunHistoryStrip({
  runs,
  runLimit,
}: Pick<ServiceWorkflow, "runs"> & { runLimit: number }) {
  const [failuresOpen, setFailuresOpen] = useState(false)
  const completed = runs.filter((run) =>
    ["Succeeded", "Failed", "TimedOut", "Aborted"].includes(run.status)
  ).length
  const succeeded = runs.filter((run) => run.status === "Succeeded").length
  return (
    <div
      role="group"
      aria-label={`Run history: ${runs.length} recent runs, newest first`}
      data-run-history
      className="w-max max-w-full space-y-2"
    >
      {runs.length ? (
        <div className="flex max-w-69 flex-wrap gap-1">
          {runs.map((run) => {
            const { icon: Icon, color } = outcomes[run.status]
            const failure = run.failureSummary
              ? ` ${failureLabels(run.failureSummary)}: ${run.failureSummary.primary.message}.`
              : ""
            const label = `${run.status} run ${run.id}, ${formatTimestamp(run.createdAt)} (${run.createdAt}).${failure} Sign-off: ${signOffDescription(run.signOff)}. Opens in a new tab.`
            return (
              <Link
                key={run.id}
                to="/runs/$id"
                params={{ id: run.id }}
                target="_blank"
                rel="noopener noreferrer"
                aria-label={label}
                title={label}
                data-run-id={run.id}
                data-run-status={run.status}
                className={`inline-flex size-6 shrink-0 items-center justify-center text-primary-foreground hover:opacity-80 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring ${color}`}
              >
                <Icon aria-hidden="true" className="size-4" />
              </Link>
            )
          })}
        </div>
      ) : (
        <p className="text-sm text-muted-foreground">No runs</p>
      )}
      <div className="flex flex-wrap gap-x-3 gap-y-1 text-xs text-muted-foreground">
        <span>
          Latest {runs.length} {runs.length === 1 ? "run" : "runs"} / up to{" "}
          {runLimit}
        </span>
        <span title="Completed runs among the recent instances shown">
          {completed ? (
            <>
              {new Intl.NumberFormat(undefined, {
                style: "percent",
                maximumFractionDigits: 1,
              }).format(succeeded / completed)}
              <span className="sr-only"> completed successfully</span>
            </>
          ) : (
            "No completed runs"
          )}
        </span>
      </div>
      {runs.some((run) => run.failureSummary) ? (
        <details
          className="max-w-sm text-xs"
          onToggle={(event) => setFailuresOpen(event.currentTarget.open)}
        >
          <summary className="cursor-pointer">
            Failure details for run instances
          </summary>
          {failuresOpen ? (
            <ul className="mt-2 space-y-2">
              {runs
                .filter((run) => run.failureSummary)
                .map((run) => (
                  <li key={run.id}>
                    <Link
                      to="/runs/$id"
                      params={{ id: run.id }}
                      className="block truncate font-mono"
                    >
                      {run.id}
                    </Link>
                    <FailureSummary
                      summary={run.failureSummary}
                      status={run.status}
                    />
                  </li>
                ))}
            </ul>
          ) : null}
          {runs.some((run) => run.signOff.latest) ? (
            <details className="max-w-sm text-xs">
              <summary className="rounded focus-visible:outline-2 focus-visible:outline-ring">
                Sign-off details for run instances
              </summary>
              <ul className="mt-2 space-y-3">
                {runs
                  .filter((run) => run.signOff.latest)
                  .map((run) => (
                    <li key={run.id}>
                      <p className="truncate font-mono" title={run.id}>
                        {run.id}
                      </p>
                      <SignOffSummary state={run.signOff} runId={run.id} />
                    </li>
                  ))}
              </ul>
            </details>
          ) : null}
        </details>
      ) : null}
    </div>
  )
}
