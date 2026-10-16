import { createFileRoute } from "@tanstack/react-router"
import { z } from "zod"
import { SeriesFiresTable } from "@/features/series/SeriesFiresTable"

export const Route = createFileRoute("/series/$id")({
  params: { parse: (params) => ({ id: z.uuid().parse(params.id) }) },
  component: SeriesHistory,
})

function SeriesHistory() {
  const { id } = Route.useParams()
  return (
    <>
      <h1 className="text-2xl font-semibold">Series history</h1>
      <p className="text-sm break-all text-muted-foreground">Series: {id}</p>
      <SeriesFiresTable seriesId={id} />
    </>
  )
}
