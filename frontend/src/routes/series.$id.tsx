import { createFileRoute } from "@tanstack/react-router"
import { z } from "zod"
import { HistoryFilters, HistoryTable } from "@/features/runs/HistoryTable"
import { historySearchSchema } from "@/features/runs/history"

export const Route = createFileRoute("/series/$id")({
  params: { parse: (params) => ({ id: z.uuid().parse(params.id) }) },
  validateSearch: historySearchSchema,
  component: SeriesHistory,
})

function SeriesHistory() {
  const { id } = Route.useParams()
  const search = Route.useSearch()
  const navigate = Route.useNavigate()
  return (
    <>
      <h1 className="text-2xl font-semibold">Series history</h1>
      <p className="text-sm break-all text-muted-foreground">Series: {id}</p>
      <HistoryFilters
        key={JSON.stringify(search)}
        search={search}
        onChange={(next) => void navigate({ search: next })}
      />
      <HistoryTable scope={{ seriesId: id }} search={search} />
    </>
  )
}
