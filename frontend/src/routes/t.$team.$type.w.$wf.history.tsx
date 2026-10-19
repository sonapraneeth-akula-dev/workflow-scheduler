import { useId } from "react"
import { createFileRoute, Link } from "@tanstack/react-router"
import { ExternalLinkIcon } from "lucide-react"
import { HistoryFilters, HistoryTable } from "@/features/runs/HistoryTable"
import { historySearchSchema } from "@/features/runs/history"
import { TriggerDialog } from "@/features/runs/TriggerDialog"
import { encodeDashboardId } from "@/features/dashboards/api"
import { WorkflowDashboardAccordion } from "@/features/dashboards/WorkflowDashboardAccordion"
import { SubscribeMenu } from "@/features/subscriptions/SubscribeMenu"
import { Button } from "@/components/ui/button"

export const Route = createFileRoute("/t/$team/$type/w/$wf/history")({
  validateSearch: historySearchSchema,
  component: WorkflowHistory,
})

function WorkflowHistory() {
  const newTabDescription = useId()
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
            <span id={newTabDescription} hidden>
              Opens in a new tab.
            </span>
            <Button variant="outline" size="sm" asChild>
              <Link
                to="/t/$team/$type/w/$wf/versions"
                params={{ team, type, wf }}
                target="_blank"
                rel="noopener noreferrer"
                aria-describedby={newTabDescription}
                title="Registered semantic versions, comparisons and activation"
              >
                Workflow versions
                <ExternalLinkIcon aria-hidden="true" data-icon="inline-end" />
              </Link>
            </Button>
            <Button variant="outline" size="sm" asChild>
              <Link
                to="/dashboards/$scope/$id"
                params={{
                  scope: "workflow",
                  id: encodeDashboardId(workflowId),
                }}
                target="_blank"
                rel="noopener noreferrer"
                aria-describedby={newTabDescription}
                title="Completed-run and task failure, duration, CPU and memory metrics"
              >
                Metrics dashboard
                <ExternalLinkIcon aria-hidden="true" data-icon="inline-end" />
              </Link>
            </Button>
          </nav>
          <TriggerDialog workflowId={workflowId} />
          <SubscribeMenu scope="workflow" scopeId={workflowId} />
        </div>
      </section>
      <WorkflowDashboardAccordion key={workflowId} workflowId={workflowId} />
      <HistoryFilters
        key={JSON.stringify(search)}
        search={search}
        onChange={(next) => void navigate({ search: next })}
      />
      <HistoryTable scope={{ workflowId }} search={search} />
    </>
  )
}
