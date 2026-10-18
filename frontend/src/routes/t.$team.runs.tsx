import { createFileRoute, Link } from "@tanstack/react-router"
import { ErrorNotice } from "@/components/ErrorNotice"
import { FilterBar } from "@/features/catalog/FilterBar"
import { RunList } from "@/features/runs/RunList"
import { runSearchFieldsSchema, runSearchSchema } from "@/features/runs/search"

export const Route = createFileRoute("/t/$team/runs")({
  validateSearch: runSearchFieldsSchema,
  component: TeamRuns,
})

function TeamRuns() {
  const { team } = Route.useParams()
  const search = Route.useSearch()
  const navigate = Route.useNavigate()
  const scoped = runSearchSchema.safeParse({ ...search, team })
  return (
    <>
      <nav aria-label="Breadcrumb" className="flex flex-wrap gap-2">
        <Link to="/runs">Teams</Link>
        <span aria-hidden="true">/</span>
        <Link to="/t/$team" params={{ team }}>
          {team}
        </Link>
      </nav>
      <h1 className="text-2xl font-semibold break-words">{team} runs</h1>
      {scoped.success ? (
        <>
          <FilterBar
            key={JSON.stringify(scoped.data)}
            team={team}
            filters={scoped.data}
            onChange={(next) => {
              const query = { ...next }
              delete query.team
              void navigate({ search: query })
            }}
          />
          <RunList search={scoped.data} />
        </>
      ) : (
        <ErrorNotice error={scoped.error} />
      )}
    </>
  )
}
