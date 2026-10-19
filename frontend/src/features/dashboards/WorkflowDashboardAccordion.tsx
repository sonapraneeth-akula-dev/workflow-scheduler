import { lazy, Suspense, useState } from "react"
import type { TrendWindow } from "@/features/series/api"
import { encodeDashboardId } from "./api"

const Dashboard = lazy(() =>
  import("./DashboardPage").then(({ DashboardPage }) => ({
    default: DashboardPage,
  }))
)

export function WorkflowDashboardAccordion({
  workflowId,
}: {
  workflowId: string
}) {
  const [open, setOpen] = useState(false)
  const [search, setSearch] = useState<{ window: TrendWindow; top: number }>({
    window: "30d",
    top: 10,
  })
  return (
    <details
      className="min-w-0 rounded border bg-card p-3 sm:p-4"
      onToggle={(event) => setOpen(event.currentTarget.open)}
    >
      <summary className="cursor-pointer font-semibold">
        Metrics dashboard
      </summary>
      {open ? (
        <div className="mt-4 min-w-0 space-y-4">
          <p className="text-sm text-muted-foreground">
            Metrics use their own UTC creation window. Run-history status,
            outcome and cancellation filters do not change these charts.
          </p>
          <Suspense
            fallback={<p role="status">Loading metrics dashboard...</p>}
          >
            <Dashboard
              scope="workflow"
              id={encodeDashboardId(workflowId)}
              {...search}
              onChange={setSearch}
              embedded
            />
          </Suspense>
        </div>
      ) : null}
    </details>
  )
}
