import { useId } from "react"
import { useInfiniteQuery, useQuery } from "@tanstack/react-query"
import { Link } from "@tanstack/react-router"
import { LayoutGridIcon, ListIcon } from "lucide-react"
import { ErrorNotice } from "@/components/ErrorNotice"
import { StatusBadge } from "@/components/StatusBadge"
import { Button } from "@/components/ui/button"
import { NativeSelect } from "@/components/ui/native-select"
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group"
import {
  accessDenied,
  getTypes,
  type CatalogWorkflow,
} from "@/features/catalog/api"
import { useCatalogLive } from "@/features/catalog/useCatalogLive"
import { formatTimestamp } from "@/lib/timestamps"
import {
  getServiceHistory,
  serviceSearchSchema,
  type ServiceCursor,
  type ServiceSearch,
  type ServiceWorkflow,
} from "./api"
import { HealthBar } from "./HealthBar"

function WorkflowActions({ workflow }: { workflow: CatalogWorkflow }) {
  return (
    <div className="flex flex-wrap gap-2">
      {workflow.latestRun ? (
        <Button asChild variant="outline">
          <Link
            to="/runs/$id"
            params={{ id: workflow.latestRun.id }}
            aria-label={`View last run for ${workflow.id}`}
          >
            View last run
          </Link>
        </Button>
      ) : (
        <Button variant="outline" disabled>
          View last run
        </Button>
      )}
      <Button asChild variant="outline">
        <Link
          to="/t/$team/$type/w/$wf/history"
          params={{
            team: workflow.team,
            type: workflow.type,
            wf: workflow.name,
          }}
          aria-label={`History for ${workflow.id}`}
        >
          History
        </Link>
      </Button>
    </div>
  )
}

function LastRun({ workflow }: { workflow: CatalogWorkflow }) {
  const run = workflow.latestRun
  return run ? (
    <time dateTime={run.createdAt} title={run.createdAt}>
      {formatTimestamp(run.createdAt)}
    </time>
  ) : (
    <span className="text-muted-foreground">Never run</span>
  )
}

function WorkflowGroup({
  team,
  type,
  entries,
  view,
}: {
  team: string
  type: string
  entries: ServiceWorkflow[]
  view: ServiceSearch["view"]
}) {
  const id = useId()
  const live = useCatalogLive(team, entries[0]!.workflow.type)
  return (
    <section
      aria-labelledby={id}
      className="min-w-0 space-y-3 border bg-card p-3 sm:p-4"
    >
      <div className="flex flex-wrap items-center justify-between gap-2 border-b pb-3">
        <h2 id={id} className="text-lg font-semibold break-words">
          {type}
        </h2>
        <span className="text-sm text-muted-foreground">
          {entries.length} loaded workflows
        </span>
      </div>
      {live.snapshot?.status === "error" ? (
        <div className="flex flex-wrap items-center gap-2">
          <p role="status" className="text-sm">
            Live updates unavailable.
          </p>
          <Button variant="outline" onClick={live.retry}>
            Retry live updates
          </Button>
        </div>
      ) : null}
      {view === "cards" ? (
        <ul className="grid min-w-0 gap-3 md:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-4">
          {entries.map(({ workflow, days }) => (
            <li
              key={workflow.id}
              className="min-w-0 space-y-4 border bg-background p-3"
            >
              <div className="flex flex-wrap items-start justify-between gap-2">
                <h3 className="min-w-0 text-base font-semibold break-words">
                  {workflow.name}
                </h3>
                <StatusBadge
                  status={workflow.latestRun?.status ?? "NeverRun"}
                />
              </div>
              <p
                className="truncate font-mono text-xs text-muted-foreground"
                title={workflow.id}
              >
                {workflow.id}
              </p>
              <div className="space-y-1 text-sm">
                <p className="font-medium">Last run</p>
                <LastRun workflow={workflow} />
              </div>
              <HealthBar days={days} />
              <WorkflowActions workflow={workflow} />
            </li>
          ))}
        </ul>
      ) : (
        <div
          role="region"
          aria-label={`${type} service table`}
          tabIndex={0}
          className="overflow-auto border"
        >
          <table className="w-full text-left text-sm">
            <caption className="sr-only">
              {type} workflow status, last run, daily health and history
            </caption>
            <thead className="border-b bg-secondary text-secondary-foreground">
              <tr>
                {[
                  "Workflow",
                  "Latest status",
                  "Last run",
                  "Health",
                  "Actions",
                ].map((label) => (
                  <th key={label} scope="col" className="p-3 font-bold">
                    {label}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {entries.map(({ workflow, days }) => (
                <tr
                  key={workflow.id}
                  className="border-b last:border-0 odd:bg-card even:bg-muted/50"
                >
                  <th scope="row" className="p-3 font-medium">
                    <span className="block">{workflow.name}</span>
                    <span className="font-mono text-xs font-normal text-muted-foreground">
                      {workflow.id}
                    </span>
                  </th>
                  <td className="p-3">
                    <StatusBadge
                      status={workflow.latestRun?.status ?? "NeverRun"}
                    />
                  </td>
                  <td className="p-3">
                    <LastRun workflow={workflow} />
                  </td>
                  <td className="min-w-64 p-3">
                    <HealthBar days={days} />
                  </td>
                  <td className="p-3">
                    <WorkflowActions workflow={workflow} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}

export function ServiceHistory({
  team,
  search,
  onChange,
}: {
  team: string
  search: ServiceSearch
  onChange: (next: ServiceSearch) => void
}) {
  const types = useQuery({
    queryKey: ["catalog", "team", team, "filter-types"],
    queryFn: ({ signal }) => getTypes(team, signal),
  })
  const query = useInfiniteQuery({
    queryKey: [
      "catalog",
      "team",
      team,
      "service-history",
      search.type,
      search.window,
    ],
    initialPageParam: null as ServiceCursor,
    queryFn: ({ pageParam, signal }) =>
      getServiceHistory(team, search.type, search.window, pageParam, signal),
    getNextPageParam: (page): ServiceCursor | undefined =>
      page.nextCursor
        ? { cursor: page.nextCursor, afterId: page.items.at(-1)!.workflow.id }
        : undefined,
  })
  const entries =
    accessDenied(query.error) || accessDenied(types.error)
      ? undefined
      : query.data?.pages.flatMap((page) => page.items)
  const groups = new Map<string, ServiceWorkflow[]>()
  for (const entry of entries ?? []) {
    const type = entry.workflow.metadata.type
    if (!groups.has(type)) groups.set(type, [])
    groups.get(type)!.push(entry)
  }
  return (
    <div className="min-w-0 space-y-4">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <h1 className="min-w-0 text-2xl font-semibold break-words">
          {team} service history
        </h1>
        <div className="flex flex-wrap items-end gap-3">
          <label className="flex min-w-0 flex-col gap-1 text-sm">
            Workflow type
            <NativeSelect
              value={search.type ?? ""}
              onChange={(event) =>
                onChange({ ...search, type: event.target.value || undefined })
              }
            >
              <option value="">All workflow types</option>
              {search.type &&
              !types.data?.some((type) => type.name === search.type) ? (
                <option value={search.type}>{search.type} (unavailable)</option>
              ) : null}
              {!accessDenied(types.error)
                ? types.data?.map((type) => (
                    <option key={type.name} value={type.name}>
                      {type.name}
                    </option>
                  ))
                : null}
            </NativeSelect>
          </label>
          <label className="flex flex-col gap-1 text-sm">
            Health window
            <NativeSelect
              value={search.window}
              onChange={(event) =>
                onChange(
                  serviceSearchSchema.parse({
                    ...search,
                    window: event.target.value,
                  })
                )
              }
            >
              <option value="7d">7 days</option>
              <option value="30d">30 days</option>
              <option value="90d">90 days</option>
            </NativeSelect>
          </label>
          <ToggleGroup
            type="single"
            value={search.view}
            aria-label="Service history format"
            onValueChange={(value) => {
              if (value)
                onChange(serviceSearchSchema.parse({ ...search, view: value }))
            }}
          >
            <ToggleGroupItem value="cards" aria-label="Card format">
              <LayoutGridIcon aria-hidden="true" />
              Cards
            </ToggleGroupItem>
            <ToggleGroupItem value="table" aria-label="Table format">
              <ListIcon aria-hidden="true" />
              Table
            </ToggleGroupItem>
          </ToggleGroup>
          <Button
            variant="outline"
            className="transition-none"
            disabled={query.isFetching || types.isFetching}
            onClick={() => void Promise.all([query.refetch(), types.refetch()])}
          >
            Refresh service history
          </Button>
        </div>
      </div>
      <p className="text-sm text-muted-foreground">
        Real run outcomes across all series and manual runs, grouped by metadata
        type. Each segment is one UTC run day, including today; days without
        runs remain neutral.
      </p>
      <div
        className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground"
        aria-label="Health legend"
      >
        {[
          ["bg-success", "Succeeded"],
          ["bg-destructive", "Failed or timed out"],
          ["bg-warning", "Aborted"],
          ["bg-info", "Active"],
          ["bg-muted-foreground/25", "No runs"],
        ].map(([color, label]) => (
          <span key={label} className="inline-flex items-center gap-1.5">
            <span className={`size-2.5 ${color}`} aria-hidden="true" />
            {label}
          </span>
        ))}
      </div>
      {query.isPending ? <p role="status">Loading service history...</p> : null}
      {query.error ? <ErrorNotice error={query.error} /> : null}
      {types.error ? <ErrorNotice error={types.error} /> : null}
      {entries?.length === 0 ? (
        <p role="status">No workflows are available in this team and type.</p>
      ) : null}
      {[...groups]
        .sort(([a], [b]) => a.localeCompare(b))
        .map(([type, entries]) => (
          <WorkflowGroup
            key={type}
            team={team}
            type={type}
            entries={entries}
            view={search.view}
          />
        ))}
      {query.hasNextPage && entries ? (
        <Button
          variant="outline"
          disabled={query.isFetching}
          onClick={() => void query.fetchNextPage()}
        >
          Load more workflows
        </Button>
      ) : null}
    </div>
  )
}
