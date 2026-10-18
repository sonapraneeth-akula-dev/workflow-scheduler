import { createFileRoute, Link } from "@tanstack/react-router"
import { SystemEvents } from "@/features/runs/SystemEvents"
import { runIdSchema } from "@/lib/live/runApi"

export const Route = createFileRoute("/runs/$id_/events")({
  params: { parse: (params) => ({ id: runIdSchema.parse(params.id) }) },
  component: EventsPage,
})

function EventsPage() {
  const { id } = Route.useParams()
  return (
    <>
      <h1 className="text-2xl font-semibold">Run System Events</h1>
      <Link to="/runs/$id" params={{ id }}>
        Back to run
      </Link>
      <p className="break-all">{id}</p>
      <SystemEvents key={id} runId={id} />
    </>
  )
}
