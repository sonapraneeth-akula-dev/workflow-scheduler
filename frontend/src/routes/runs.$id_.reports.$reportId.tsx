import { createFileRoute } from "@tanstack/react-router"
import { z } from "zod"
import { ReportView } from "@/features/reports/ReportView"

export const Route = createFileRoute("/runs/$id_/reports/$reportId")({
  params: {
    parse: (params) => ({
      id: z.uuid().parse(params.id),
      reportId: z.uuid().parse(params.reportId),
    }),
  },
  component: RunReport,
})

function RunReport() {
  const { id, reportId } = Route.useParams()
  return <ReportView runId={id} reportId={reportId} />
}
