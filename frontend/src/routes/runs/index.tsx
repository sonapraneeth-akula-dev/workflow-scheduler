import { createFileRoute } from "@tanstack/react-router"
import { FilterBar } from "@/features/catalog/FilterBar"
import { RunList } from "@/features/runs/RunList"
import { runSearchSchema } from "@/features/runs/search"

export const Route = createFileRoute("/runs/")({
  validateSearch: runSearchSchema,
  component: RunSearch,
})

function RunSearch() {
  const search = Route.useSearch()
  const navigate = Route.useNavigate()
  return (
    <>
      <h1 className="text-2xl font-semibold">Runs</h1>
      <FilterBar
        key={JSON.stringify(search)}
        filters={search}
        onChange={(next) => void navigate({ search: next })}
      />
      <RunList search={search} />
    </>
  )
}
