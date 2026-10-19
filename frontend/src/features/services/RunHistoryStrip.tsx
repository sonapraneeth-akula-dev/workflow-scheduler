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
  days,
  runs,
}: Pick<ServiceWorkflow, "days" | "runs">) {
  const total = days.reduce(
    (sum, day) =>
      sum +
      Object.values(day.counts).reduce((count, value) => count + value, 0),
    0
  )
  const completed = days.reduce(
    (sum, { counts }) =>
      sum +
      (counts.Succeeded ?? 0) +
      (counts.Failed ?? 0) +
      (counts.TimedOut ?? 0) +
      (counts.Aborted ?? 0),
    0
  )
  const succeeded = days.reduce(
    (sum, day) => sum + (day.counts.Succeeded ?? 0),
    0
  )
  return (
    <div
      role="group"
      aria-label={`Run history: ${total} runs in ${days.length} UTC days, newest first`}
      data-run-history
      className="min-w-40 space-y-2"
    >
      {runs.length ? (
        <div className="flex flex-wrap gap-1">
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
        <p className="text-sm text-muted-foreground">
          No runs in the last {days.length} days
        </p>
      )}
      <div className="flex flex-wrap justify-between gap-x-3 gap-y-1 text-xs text-muted-foreground">
        <span>
          {total > runs.length ? `Latest ${runs.length} of ${total}` : total}{" "}
          {total === 1 ? "run" : "runs"} / {days.length} days
        </span>
        <span title="All completed runs in this window, including those not shown">
          {completed
            ? `${new Intl.NumberFormat(undefined, { style: "percent", maximumFractionDigits: 1 }).format(succeeded / completed)} completed successfully`
            : "No completed runs"}
        </span>
      </div>
    </div>
  )
}
