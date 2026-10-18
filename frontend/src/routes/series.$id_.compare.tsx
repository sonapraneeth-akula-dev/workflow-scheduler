import { createFileRoute } from "@tanstack/react-router"
import { z } from "zod"
import { ReportCompare } from "@/features/reports/ReportCompare"

export const Route = createFileRoute("/series/$id_/compare")({
  params: { parse: (params) => ({ id: z.uuid().parse(params.id) }) },
  component: SeriesCompare,
})

function SeriesCompare() {
  const { id } = Route.useParams()
  return <ReportCompare key={id} seriesId={id} />
}
