import { createFileRoute, redirect } from "@tanstack/react-router"
import { TeamList } from "@/features/catalog/TeamList"
import { runSearchSchema } from "@/features/runs/search"

export const Route = createFileRoute("/runs/")({
  validateSearch: runSearchSchema,
  beforeLoad: ({ search }) => {
    const team = search.team ?? search.workflowId?.split("/")[0]
    if (team) {
      const next = { ...search }
      delete next.team
      throw redirect({
        to: "/t/$team/runs",
        params: { team },
        search: next,
        replace: true,
      })
    }
  },
  component: RunSearch,
})

function RunSearch() {
  const search = Route.useSearch()
  return (
    <>
      <h1 className="text-2xl font-semibold">Runs</h1>
      <p>
        Choose a team to see its runs. Teams are never mixed in one listing.
      </p>
      <TeamList destination="runs" search={search} />
    </>
  )
}
