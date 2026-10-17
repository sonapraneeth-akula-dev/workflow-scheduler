import { useQuery } from "@tanstack/react-query"
import { Link } from "@tanstack/react-router"
import { Button } from "@/components/ui/button"
import { ErrorNotice } from "@/components/ErrorNotice"
import { accessDenied, getTypes, statusSchema, type CatalogType } from "./api"
import { StatusBadge } from "@/components/StatusBadge"
import { useCatalogLive } from "./useCatalogLive"
import { orderTypes, usePresentation } from "./usePresentation"
import { PresentationFilters } from "./PresentationFilters"

export function TeamArea({ team }: { team: string }) {
  const presentation = usePresentation(team)
  const filters = presentation.data?.defaultFilters ?? {}
  const query = useQuery({
    queryKey: ["catalog", "team", team, "types", filters],
    queryFn: ({ signal }) => getTypes(team, signal, {}, true),
    enabled: presentation.data !== undefined,
  })
  const types =
    accessDenied(query.error) || presentation.data === undefined
      ? undefined
      : query.data
  return (
    <>
      <nav aria-label="Breadcrumb">
        <Link to="/">Teams</Link>
      </nav>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="min-w-0 text-2xl font-semibold break-words">{team}</h1>
        <Button
          variant="outline"
          disabled={query.isFetching || presentation.isFetching}
          onClick={() =>
            void Promise.all([query.refetch(), presentation.refetch()])
          }
        >
          Refresh types
        </Button>
      </div>
      <p>Types and latest workflow status in this team.</p>
      <PresentationFilters config={presentation.data ?? null} />
      {query.isPending ? <p role="status">Loading types...</p> : null}
      {query.error ? <ErrorNotice error={query.error} /> : null}
      {presentation.error ? <ErrorNotice error={presentation.error} /> : null}
      {types?.length === 0 ? (
        <p role="status">No permitted types in this team.</p>
      ) : null}
      {types && types.length > 0 ? (
        <ul className="grid gap-4 sm:grid-cols-2">
          {orderTypes(types, presentation.data ?? null).map((type) => (
            <TypeCard key={type.name} type={type} />
          ))}
        </ul>
      ) : null}
    </>
  )
}

function TypeCard({ type }: { type: CatalogType }) {
  const live = useCatalogLive(type.team, type.name)
  return (
    <li className="flex min-w-0 flex-col items-start gap-3 rounded-md border p-4">
      <h2 className="max-w-full text-xl font-semibold break-words">
        <Link
          to="/t/$team/$type"
          params={{ team: type.team, type: type.name }}
          className="underline"
        >
          {type.team} / {type.name}
        </Link>
      </h2>
      <p className="break-words">{type.description}</p>
      <div className="flex flex-wrap gap-2" aria-label="Workflow status counts">
        {statusSchema.options.map((status) => {
          const count = type.latestStatusCounts[status]
          return count === undefined ? null : (
            <span key={status} className="flex items-center gap-1">
              <StatusBadge status={status} /> {count}
            </span>
          )
        })}
        {Object.keys(type.latestStatusCounts).length === 0 ? (
          <span>No workflows.</span>
        ) : null}
      </div>
      {live.snapshot?.status === "error" ? (
        <>
          <p role="status">Live updates unavailable. Refresh types or retry.</p>
          <Button
            variant="outline"
            className="h-auto max-w-full whitespace-normal"
            onClick={live.retry}
          >
            Retry live updates for {type.team} / {type.name}
          </Button>
        </>
      ) : (
        <p role="status">
          {live.snapshot?.status === "live"
            ? "Live updates connected."
            : "Connecting live updates..."}
        </p>
      )}
    </li>
  )
}
