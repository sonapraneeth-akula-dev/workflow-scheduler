import { createFileRoute, Link } from "@tanstack/react-router"
import { HistoryFilters, HistoryTable } from "@/features/runs/HistoryTable"
import { historySearchSchema } from "@/features/runs/history"
import { TriggerDialog } from "@/features/runs/TriggerDialog"
import { encodeDashboardId } from "@/features/dashboards/api"
import { SubscribeMenu } from "@/features/subscriptions/SubscribeMenu"

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
      <h1 className="text-2xl font-semibold">Workflow history</h1>
      <p className="break-words">{workflowId}</p>
      <Link to="/t/$team/$type/w/$wf/versions" params={{ team, type, wf }}>
        Versions
      </Link>
      <Link
        to="/dashboards/$scope/$id"
        params={{ scope: "workflow", id: encodeDashboardId(workflowId) }}
      >
        Workflow dashboard
      </Link>
      <TriggerDialog workflowId={workflowId} />
      <SubscribeMenu scope="workflow" scopeId={workflowId} />
      <HistoryFilters
        key={JSON.stringify(search)}
        search={search}
        onChange={(next) => void navigate({ search: next })}
      />
      <HistoryTable scope={{ workflowId }} search={search} />
    </>
  )
}
