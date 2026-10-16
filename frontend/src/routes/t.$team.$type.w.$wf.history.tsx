import { createFileRoute, Link } from "@tanstack/react-router"
import { HistoryFilters, HistoryTable } from "@/features/runs/HistoryTable"
import { historySearchSchema } from "@/features/runs/history"
import { TriggerDialog } from "@/features/runs/TriggerDialog"

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
      <TriggerDialog workflowId={workflowId} />
      <HistoryFilters
        key={JSON.stringify(search)}
        search={search}
        onChange={(next) => void navigate({ search: next })}
      />
      <HistoryTable scope={{ workflowId }} search={search} />
    </>
  )
}
