import { createFileRoute, Link } from "@tanstack/react-router"
import { MachineList } from "@/features/machines/MachineList"

export const Route = createFileRoute("/machines/$id")({
  component: MachinePage,
})

function MachinePage() {
  const { id } = Route.useParams()
  return (
    <>
      <Link to="/machines" search={{}} className="underline">
        Back to machines
      </Link>
      <Link to="/dashboards/$scope/$id" params={{ scope: "machine", id }}>
        Machine dashboard
      </Link>
      <MachineList id={id} />
    </>
  )
}
