import { useId } from "react"
import { useInfiniteQuery, useQueries, useQuery } from "@tanstack/react-query"
import { Link } from "@tanstack/react-router"
import { ExternalLinkIcon, LayoutGridIcon, ListIcon } from "lucide-react"
import { ErrorNotice } from "@/components/ErrorNotice"
import { StatusBadge } from "@/components/StatusBadge"
import { Button } from "@/components/ui/button"
import { NativeSelect } from "@/components/ui/native-select"
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group"
import {
  accessDenied,
  getTypes,
  type CatalogWorkflow,
  type Presentation,
} from "@/features/catalog/api"
import {
  presentationLabel,
  presentationName,
  usePresentation,
} from "@/features/catalog/usePresentation"
import { useCatalogLive } from "@/features/catalog/useCatalogLive"
import { formatTimestamp } from "@/lib/timestamps"
import { useServiceHistoryPreferences } from "@/lib/ServiceHistoryPreferences"
import {
  getServiceHistory,
  serviceSearchSchema,
  type ServiceCursor,
  type ServiceSearch,
  type ServiceWorkflow,
} from "./api"
import { RunHistoryStrip } from "./RunHistoryStrip"
import { FailureSummary } from "@/features/runs/FailureSummary"
import { getHistory } from "@/features/runs/history"
import { ScheduleTracking } from "@/features/schedules/ScheduleTracking"
import { defaultTracking, trackingSearch } from "@/features/schedules/tracking"

function WorkflowActions({ workflow }: { workflow: CatalogWorkflow }) {
  const newTabDescription = useId()
  const history = useServiceHistoryPreferences()
  const tracking = history.tracking[workflow.id] ?? defaultTracking
  return (
    <div className="flex flex-wrap gap-2">
      <span id={newTabDescription} hidden>
        Opens in a new tab.
      </span>
      {workflow.latestRun ? (
        <Button asChild variant="outline">
          <Link
            to="/runs/$id"
            params={{ id: workflow.latestRun.id }}
            target="_blank"
            rel="noopener noreferrer"
            aria-label={`View last run for ${workflow.id}`}
            aria-describedby={newTabDescription}
          >
            View last run
            <ExternalLinkIcon aria-hidden="true" data-icon="inline-end" />
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
          search={trackingSearch(tracking)}
          target="_blank"
          rel="noopener noreferrer"
          aria-label={`History for ${workflow.id}`}
          aria-describedby={newTabDescription}
        >
          History
          <ExternalLinkIcon aria-hidden="true" data-icon="inline-end" />
        </Link>
      </Button>
    </div>
  )
}

function LastRun({ workflow }: { workflow: CatalogWorkflow }) {
  const run = workflow.latestRun
  const history = useServiceHistoryPreferences()
  const selection = history.tracking[workflow.id] ?? defaultTracking
  const filtered = selection.seriesIds !== null || !selection.includeManual
  return run ? (
    <time dateTime={run.createdAt} title={run.createdAt}>
      {formatTimestamp(run.createdAt)}
    </time>
  ) : (
    <span className="text-muted-foreground">
      {filtered ? "No tracked runs" : "Never run"}
    </span>
  )
}

type TrackedWorkflow = ServiceWorkflow & {
  trackingPending?: boolean
  trackingError?: Error | null
}

function WorkflowTracking({
  entry,
  runLimit,
}: {
  entry: TrackedWorkflow
  runLimit: number
}) {
  const history = useServiceHistoryPreferences()
  return (
    <div className="flex min-w-0 flex-col gap-3">
      <ScheduleTracking
        workflowId={entry.workflow.id}
        schedules={entry.schedules ?? []}
        selection={history.tracking[entry.workflow.id] ?? defaultTracking}
        onChange={(next) => {
          history.setTracking(entry.workflow.id, next)
        }}
      />
      {entry.trackingError ? (
        <ErrorNotice error={entry.trackingError} />
      ) : entry.trackingPending ? (
        <p role="status">Loading tracked runs...</p>
      ) : (
        <RunHistoryStrip runs={entry.runs} runLimit={runLimit} />
      )}
    </div>
  )
}

function WorkflowStatus({ entry }: { entry: TrackedWorkflow }) {
  const history = useServiceHistoryPreferences()
  const selection = history.tracking[entry.workflow.id] ?? defaultTracking
  if (entry.trackingPending)
    return <span className="text-sm text-muted-foreground">Loading...</span>
  if (entry.trackingError)
    return <span className="text-sm text-muted-foreground">Unavailable</span>
  if (
    !entry.workflow.latestRun &&
    (selection.seriesIds !== null || !selection.includeManual)
  )
    return (
      <span className="text-sm text-muted-foreground">No tracked runs</span>
    )
  return <StatusBadge status={entry.workflow.latestRun?.status ?? "NeverRun"} />
}

function WorkflowTypeLive({ team, type }: { team: string; type: string }) {
  const live = useCatalogLive(team, type)
  return live.snapshot?.status === "error" ? (
    <div className="flex flex-wrap items-center gap-2">
      <p role="status">Live updates unavailable for {type}.</p>
      <Button variant="outline" onClick={live.retry}>
        Retry live updates for {type}
      </Button>
    </div>
  ) : null
}

function WorkflowGroup({
  label,
  entries,
  view,
  runLimit,
  presentation,
}: {
  label: string
  entries: TrackedWorkflow[]
  view: ServiceSearch["view"]
  runLimit: number
  presentation: Presentation | null
}) {
  const id = useId()
  return (
    <section
      aria-labelledby={id}
      className="min-w-0 space-y-3 border bg-card p-3 sm:p-4"
    >
      <div className="flex flex-wrap items-center justify-between gap-2 border-b pb-3">
        <h2 id={id} className="text-lg font-semibold break-words">
          {label}
        </h2>
        <span className="text-sm text-muted-foreground">
          {entries.length} loaded workflows
        </span>
      </div>
      {view === "cards" ? (
        <ul className="grid min-w-0 items-start gap-3 md:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-4">
          {entries.map((entry) => {
            const { workflow } = entry
            return (
              <li
                key={workflow.id}
                className="min-w-0 space-y-4 border bg-background p-3"
              >
                <div className="flex flex-wrap items-start justify-between gap-2">
                  <h3 className="min-w-0 text-base font-semibold break-words">
                    {presentationName(presentation, workflow.name, workflow.id)}
                  </h3>
                  <WorkflowStatus entry={entry} />
                </div>
                <p
                  className="truncate font-mono text-xs text-muted-foreground"
                  title={workflow.id}
                >
                  {workflow.id}
                </p>
                <FailureSummary
                  summary={workflow.latestRun?.failureSummary}
                  status={workflow.latestRun?.status}
                />
                <p className="text-sm">
                  <span className="font-medium">Last run:</span>{" "}
                  {entry.trackingPending ? (
                    "Loading..."
                  ) : entry.trackingError ? (
                    "Unavailable"
                  ) : (
                    <LastRun workflow={workflow} />
                  )}
                </p>
                <WorkflowTracking entry={entry} runLimit={runLimit} />
                <WorkflowActions workflow={workflow} />
              </li>
            )
          })}
        </ul>
      ) : (
        <div
          role="region"
          aria-label={`${label} service table`}
          tabIndex={0}
          className="relative overflow-auto border"
        >
          <table className="w-full text-left text-sm">
            <caption className="sr-only">
              {label} workflow status, last run, run instances and history
            </caption>
            <thead className="border-b bg-secondary text-secondary-foreground">
              <tr>
                {[
                  "Workflow",
                  "Latest status",
                  "Last run",
                  "Run instances",
                  "Actions",
                ].map((label) => (
                  <th key={label} scope="col" className="p-3 font-bold">
                    {label}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {entries.map((entry) => {
                const { workflow } = entry
                return (
                  <tr
                    key={workflow.id}
                    className="border-b last:border-0 odd:bg-card even:bg-muted/50"
                  >
                    <th scope="row" className="p-3 font-medium">
                      <span className="block">
                        {presentationName(
                          presentation,
                          workflow.name,
                          workflow.id
                        )}
                      </span>
                      <span className="font-mono text-xs font-normal text-muted-foreground">
                        {workflow.id}
                      </span>
                    </th>
                    <td className="p-3">
                      <WorkflowStatus entry={entry} />
                      <FailureSummary
                        summary={workflow.latestRun?.failureSummary}
                        status={workflow.latestRun?.status}
                      />
                    </td>
                    <td className="p-3">
                      {entry.trackingPending ? (
                        "Loading..."
                      ) : entry.trackingError ? (
                        "Unavailable"
                      ) : (
                        <LastRun workflow={workflow} />
                      )}
                    </td>
                    <td className="w-px p-3">
                      <WorkflowTracking entry={entry} runLimit={runLimit} />
                    </td>
                    <td className="p-3">
                      <WorkflowActions workflow={workflow} />
                    </td>
                  </tr>
                )
              })}
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
  const history = useServiceHistoryPreferences()
  const presentation = usePresentation(team)
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
      history.runLimit,
    ],
    initialPageParam: null as ServiceCursor,
    queryFn: ({ pageParam, signal }) =>
      getServiceHistory(team, search.type, history.runLimit, pageParam, signal),
    getNextPageParam: (page): ServiceCursor | undefined =>
      page.nextCursor
        ? { cursor: page.nextCursor, afterId: page.items.at(-1)!.workflow.id }
        : undefined,
  })
  const entries =
    accessDenied(query.error) || accessDenied(types.error)
      ? undefined
      : query.data?.pages.flatMap((page) => page.items)
  const filteredEntries = (entries ?? []).filter((entry) => {
    const selection = history.tracking[entry.workflow.id] ?? defaultTracking
    return selection.seriesIds !== null || !selection.includeManual
  })
  const trackedQueries = useQueries({
    queries: filteredEntries.map((entry) => {
      const filters = {
        ...trackingSearch(
          history.tracking[entry.workflow.id] ?? defaultTracking
        ),
        outcome: "Run" as const,
      }
      return {
        queryKey: [
          "catalog",
          "team",
          team,
          "service-history",
          entry.workflow.type,
          "tracking",
          entry.workflow.id,
          filters,
          history.runLimit,
        ],
        queryFn: ({ signal }: { signal: AbortSignal }) =>
          getHistory(
            { workflowId: entry.workflow.id },
            filters,
            null,
            signal,
            history.runLimit
          ),
      }
    }),
  })
  const trackedEntries = new Map(
    filteredEntries.map((entry, index) => {
      const tracked = trackedQueries[index]!
      const runs = (tracked.error ? [] : (tracked.data?.items ?? [])).map(
        (row) => {
          if (!row.runId || !row.status)
            throw new Error("Tracked run history returned a non-run row.")
          return {
            id: row.runId,
            status: row.status,
            createdAt: row.at,
            startedAt: row.startedAt,
            endedAt: row.endedAt,
            failureSummary: row.failureSummary,
          }
        }
      )
      return [
        entry.workflow.id,
        {
          ...entry,
          workflow: { ...entry.workflow, latestRun: runs[0] ?? null },
          runs,
          trackingPending: tracked.isPending,
          trackingError: tracked.error,
        },
      ]
    })
  )
  const config =
    accessDenied(query.error) || accessDenied(types.error)
      ? null
      : (presentation.data ?? null)
  const metadataKeys = new Set<string>()
  if (!accessDenied(query.error) && !accessDenied(types.error)) {
    for (const type of types.data ?? [])
      if (!search.type || type.name === search.type)
        for (const field of type.metadataSchema.keys)
          metadataKeys.add(field.key)
    for (const entry of entries ?? [])
      for (const key of Object.keys(entry.workflow.metadata.values))
        metadataKeys.add(key)
  }
  const groups = new Map<string | undefined, TrackedWorkflow[]>()
  for (const original of entries ?? []) {
    const entry = trackedEntries.get(original.workflow.id) ?? original
    const key = search.groupKey
    const value =
      search.groupBy === "type"
        ? entry.workflow.metadata.type
        : key && Object.hasOwn(entry.workflow.metadata.values, key)
          ? entry.workflow.metadata.values[key]
          : undefined
    if (!groups.has(value)) groups.set(value, [])
    groups.get(value)!.push(entry)
  }
  return (
    <div className="min-w-0 space-y-4">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div className="min-w-0">
          <h1 className="text-2xl font-semibold break-words">
            {presentationName(config, team)} Service History
          </h1>
          <p className="text-sm text-muted-foreground">
            Run instances: latest {history.runLimit} runs per workflow
          </p>
        </div>
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
                      {presentationName(
                        config,
                        type.name,
                        `${team}/${type.name}`
                      )}
                    </option>
                  ))
                : null}
            </NativeSelect>
          </label>
          <label className="flex min-w-0 flex-col gap-1 text-sm">
            Container grouping
            <NativeSelect
              value={
                search.groupBy === "metadata"
                  ? `metadata:${search.groupKey}`
                  : "type"
              }
              onChange={(event) => {
                const value = event.target.value
                onChange(
                  serviceSearchSchema.parse({
                    ...search,
                    groupBy: value === "type" ? "type" : "metadata",
                    groupKey:
                      value === "type"
                        ? undefined
                        : value.slice("metadata:".length),
                  })
                )
              }}
            >
              <option value="type">Workflow type</option>
              {search.groupBy === "metadata" &&
              search.groupKey &&
              !metadataKeys.has(search.groupKey) ? (
                <option value={`metadata:${search.groupKey}`}>
                  {presentationName(
                    null,
                    presentationLabel(config, search.groupKey)
                  )}{" "}
                  (unavailable)
                </option>
              ) : null}
              {[...metadataKeys]
                .sort((a, b) => a.localeCompare(b))
                .map((key) => (
                  <option key={key} value={`metadata:${key}`}>
                    {key === "type"
                      ? `${presentationName(null, presentationLabel(config, key))} (metadata value)`
                      : presentationName(null, presentationLabel(config, key))}
                  </option>
                ))}
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
            disabled={
              query.isFetching ||
              types.isFetching ||
              trackedQueries.some((tracked) => tracked.isFetching)
            }
            onClick={() =>
              void Promise.all([
                query.refetch(),
                types.refetch(),
                presentation.refetch(),
                ...trackedQueries.map((tracked) => tracked.refetch()),
              ])
            }
          >
            Refresh service history
          </Button>
        </div>
      </div>
      <p className="text-sm text-muted-foreground">
        Real run outcomes from each workflow's selected sources, grouped by{" "}
        {search.groupBy === "metadata"
          ? `metadata key "${presentationName(null, presentationLabel(config, search.groupKey!))}"`
          : "workflow type"}
        . Each colored icon is one run, newest first; select it to open that
        instance in a new tab. Up to {history.runLimit} recent runs per workflow
        are shown, regardless of age; change this in your account Settings.
        History contains the full matching list. Success rates use the completed
        runs shown. Choose one or multiple named schedules for each workflow;
        its History link uses the same selection.
      </p>
      <div
        className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground"
        aria-label="Run status legend"
      >
        {[
          ["bg-success", "Succeeded"],
          ["bg-destructive", "Failed or timed out"],
          ["bg-warning", "Aborted"],
          ["bg-info", "Queued, running or aborting"],
        ].map(([color, label]) => (
          <span key={label} className="inline-flex items-center gap-1.5">
            <span className={`size-2.5 ${color}`} aria-hidden="true" />
            {label}
          </span>
        ))}
      </div>
      {history.error ? <ErrorNotice error={history.error} /> : null}
      {history.trackingError ? (
        <ErrorNotice error={history.trackingError} />
      ) : null}
      {query.isPending ? <p role="status">Loading service history...</p> : null}
      {query.error ? <ErrorNotice error={query.error} /> : null}
      {types.error ? <ErrorNotice error={types.error} /> : null}
      {presentation.error ? <ErrorNotice error={presentation.error} /> : null}
      {entries?.length === 0 ? (
        <p role="status">No workflows are available in this team and type.</p>
      ) : null}
      {[...new Set(entries?.map(({ workflow }) => workflow.type))]
        .sort()
        .map((type) => (
          <WorkflowTypeLive key={`${team}/${type}`} team={team} type={type} />
        ))}
      {[...groups]
        .sort(([a], [b]) =>
          a === undefined ? 1 : b === undefined ? -1 : a.localeCompare(b)
        )
        .map(([value, entries]) => (
          <WorkflowGroup
            presentation={config}
            runLimit={history.runLimit}
            key={value === undefined ? "missing" : JSON.stringify(value)}
            label={
              value === undefined
                ? "Not set"
                : value.trim().length === 0
                  ? `Empty value (${JSON.stringify(value)})`
                  : presentationName(
                      config,
                      value,
                      search.groupBy === "type" ? `${team}/${value}` : value
                    )
            }
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
