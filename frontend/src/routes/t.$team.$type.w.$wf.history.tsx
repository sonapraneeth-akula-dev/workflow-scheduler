import { createFileRoute } from "@tanstack/react-router"
import { HistoryFilters, HistoryTable } from "@/features/runs/HistoryTable"
import { historySearchSchema } from "@/features/runs/history"

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
      <HistoryFilters
        key={JSON.stringify(search)}
        search={search}
        onChange={(next) => void navigate({ search: next })}
      />
      <HistoryTable scope={{ workflowId }} search={search} />
    </>
  )
}
