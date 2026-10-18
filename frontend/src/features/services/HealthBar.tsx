import { useState } from "react"
import { Link } from "@tanstack/react-router"
import { healthState, healthSummary, type HealthDay } from "./api"

const colors = {
  Failed: "bg-destructive",
  Aborted: "bg-warning",
  Running: "bg-info",
  Succeeded: "bg-success",
  NoRuns: "bg-muted-foreground/25",
}

export function HealthBar({
  team,
  workflowId,
  days,
}: {
  team: string
  workflowId: string
  days: HealthDay[]
}) {
  const [expanded, setExpanded] = useState(false)
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
    <div className="min-w-40 space-y-2">
      <div
        role="img"
        aria-label={`${days.length} UTC days: ${total} runs, ${succeeded} succeeded, ${completed - succeeded} other completed outcomes. No-run days are neutral.`}
        className="flex h-6 gap-0.5"
      >
        {days.map((day) => (
          <span
            key={day.day}
            data-health-day={day.day}
            data-health-state={healthState(day.counts)}
            className={`min-w-0 flex-1 ${colors[healthState(day.counts)]}`}
            title={`${day.day} UTC: ${healthSummary(day.counts)}`}
          />
        ))}
      </div>
      <div className="flex flex-wrap justify-between gap-x-3 gap-y-1 text-xs text-muted-foreground">
        <span>{days.length} days / UTC</span>
        <span>
          {completed
            ? `${new Intl.NumberFormat(undefined, { style: "percent", maximumFractionDigits: 1 }).format(succeeded / completed)} completed successfully`
            : "No completed runs"}
        </span>
      </div>
      <details
        open={expanded}
        onToggle={(event) => setExpanded(event.currentTarget.open)}
      >
        <summary className="w-fit text-xs">Daily health details</summary>
        {expanded ? (
          <ol className="mt-2 max-h-48 space-y-1 overflow-auto text-xs">
            {days.map((day) => (
              <li key={day.day}>
                <Link
                  to="/t/$team/runs"
                  params={{ team }}
                  search={{
                    workflowId,
                    from: `${day.day}T00:00:00Z`,
                    to: new Date(Date.parse(day.day) + 86400000).toISOString(),
                  }}
                  className="underline"
                >
                  {day.day} UTC: {healthSummary(day.counts)}
                </Link>
              </li>
            ))}
          </ol>
        ) : null}
      </details>
    </div>
  )
}
