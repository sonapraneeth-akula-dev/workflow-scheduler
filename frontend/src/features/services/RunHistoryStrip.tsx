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

const outcomes = {
  Queued: { icon: Clock, color: "bg-info" },
  Running: { icon: Play, color: "bg-info" },
  Aborting: { icon: OctagonPause, color: "bg-info" },
  Succeeded: { icon: CircleCheck, color: "bg-success" },
  Failed: { icon: CircleX, color: "bg-destructive" },
  TimedOut: { icon: AlarmClock, color: "bg-destructive" },
  Aborted: { icon: Ban, color: "bg-warning" },
}

export function RunHistoryStrip({
  runs,
  runLimit,
}: Pick<ServiceWorkflow, "runs"> & { runLimit: number }) {
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
            const label = `${run.status} run ${run.id}, ${formatTimestamp(run.createdAt)} (${run.createdAt}). Opens in a new tab.`
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
    </div>
  )
}
