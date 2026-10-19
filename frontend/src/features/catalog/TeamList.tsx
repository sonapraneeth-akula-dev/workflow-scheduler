import { useQuery } from "@tanstack/react-query"
import { Link } from "@tanstack/react-router"
import { Button } from "@/components/ui/button"
import { ErrorNotice } from "@/components/ErrorNotice"
import { accessDenied, getTeams } from "./api"
import type { RunSearch } from "@/features/runs/search"
import { presentationName } from "./usePresentation"

export function TeamList({
  destination = "catalog",
  search,
}: {
  destination?: "catalog" | "runs" | "services"
  search?: RunSearch
}) {
  const query = useQuery({
    queryKey: ["catalog", "teams"],
    queryFn: ({ signal }) => getTeams(signal),
  })
  const teams = accessDenied(query.error) ? undefined : query.data
  return (
    <section aria-label="Your teams" className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="text-xl font-semibold">Your teams</h2>
        <Button
          variant="outline"
          disabled={query.isFetching}
          onClick={() => void query.refetch()}
        >
          Refresh teams
        </Button>
      </div>
      {query.isPending ? <p role="status">Loading teams...</p> : null}
      {query.error ? <ErrorNotice error={query.error} /> : null}
      {teams?.length === 0 ? (
        <p role="status">No teams are available to your account.</p>
      ) : null}
      {teams && teams.length > 0 ? (
        <ul className="grid gap-4 sm:grid-cols-2">
          {teams.map((team) => (
            <li key={team.team} className="min-w-0 rounded-md border p-4">
              <Link
                to={
                  destination === "services"
                    ? "/t/$team/services"
                    : destination === "runs"
                      ? "/t/$team/runs"
                      : "/t/$team"
                }
                params={{ team: team.team }}
                search={search ?? {}}
                className="break-words underline"
              >
                {presentationName(team.presentation, team.team)}
              </Link>
              <p>
                {team.typeCount} permitted{" "}
                {team.typeCount === 1 ? "type" : "types"}
              </p>
            </li>
          ))}
        </ul>
      ) : null}
    </section>
  )
}
