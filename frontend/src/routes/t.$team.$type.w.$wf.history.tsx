import { createFileRoute, Link } from "@tanstack/react-router"
import { HistoryFilters, HistoryTable } from "@/features/runs/HistoryTable"
import { historySearchSchema } from "@/features/runs/history"
import { TriggerDialog } from "@/features/runs/TriggerDialog"
import { encodeDashboardId } from "@/features/dashboards/api"
import { SubscribeMenu } from "@/features/subscriptions/SubscribeMenu"
import { Button } from "@/components/ui/button"

export const Route = createFileRoute("/t/$team/$type/w/$wf/history")({
  validateSearch: historySearchSchema,
  component: WorkflowHistory,
})

function WorkflowHistory() {
  const { team, type, wf } = Route.useParams()
  const search = Route.useSearch()
  const navigate = Route.useNavigate()
  const workflowId = `${team}/${type}/${wf}`
  return (
    <>
      <section
        aria-label="Workflow history summary"
        className="flex min-w-0 flex-wrap items-center justify-between gap-3"
      >
        <div className="min-w-0">
          <h1 className="text-2xl font-semibold">Workflow history</h1>
          <p className="text-sm break-words text-muted-foreground">
            {workflowId}
          </p>
        </div>
        <div className="flex min-w-0 flex-wrap items-center gap-2">
          <nav
            aria-label="Related workflow pages"
            className="flex flex-wrap gap-2"
          >
            <Button variant="outline" size="sm" asChild>
              <Link
                to="/t/$team/$type/w/$wf/versions"
                params={{ team, type, wf }}
                title="Registered semantic versions, comparisons and activation"
              >
                Workflow versions
              </Link>
            </Button>
            <Button variant="outline" size="sm" asChild>
              <Link
                to="/dashboards/$scope/$id"
                params={{
                  scope: "workflow",
                  id: encodeDashboardId(workflowId),
                }}
                title="Completed-run and task failure, duration, CPU and memory metrics"
              >
                Metrics dashboard
              </Link>
            </Button>
          </nav>
          <TriggerDialog workflowId={workflowId} />
          <SubscribeMenu scope="workflow" scopeId={workflowId} />
        </div>
      </section>
      <HistoryFilters
        key={JSON.stringify(search)}
        search={search}
        onChange={(next) => void navigate({ search: next })}
      />
      <HistoryTable scope={{ workflowId }} search={search} />
    </>
  )
}
