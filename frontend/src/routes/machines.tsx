import { createFileRoute, Outlet, useMatchRoute } from "@tanstack/react-router"
import { MachineFilters, MachineList } from "@/features/machines/MachineList"
import { machineSearchSchema } from "@/features/machines/api"

export const Route = createFileRoute("/machines")({
  validateSearch: machineSearchSchema,
  component: MachinesPage,
})

function MachinesPage() {
  const search = Route.useSearch()
  const navigate = Route.useNavigate()
  const match = useMatchRoute()
  return (
    <>
      <h1 className="text-2xl font-semibold">Machines</h1>
      {match({ to: "/machines/$id", fuzzy: true }) ? (
        <Outlet />
      ) : (
        <>
          <MachineFilters
            key={JSON.stringify(search)}
            search={search}
            onChange={(next) => void navigate({ search: next })}
          />
          <MachineList search={search} />
        </>
      )}
    </>
  )
}
