import { useInfiniteQuery } from "@tanstack/react-query"
import { Link } from "@tanstack/react-router"
import { Button } from "@/components/ui/button"
import { ErrorNotice } from "@/components/ErrorNotice"
import { accessDenied, getWorkflows } from "./api"
import { StatusBadge } from "@/components/StatusBadge"
import { useCatalogLive } from "./useCatalogLive"
import {
  groupWorkflows,
  metadataValue,
  presentationLabel,
  usePresentation,
} from "./usePresentation"
import { PresentationFilters } from "./PresentationFilters"

export function TypeWorkflows({ team, type }: { team: string; type: string }) {
  const live = useCatalogLive(team, type)
  const presentation = usePresentation(team)
  const config = presentation.data ?? null
  const filters = config?.defaultFilters ?? {}
  const columns = config?.listColumns ?? []
  const query = useInfiniteQuery({
    queryKey: ["catalog", "team", team, "workflows", type, filters],
    initialPageParam: null,
    queryFn: ({
      pageParam,
      signal,
    }: {
      pageParam: string | null
      signal: AbortSignal
    }) => getWorkflows(team, type, pageParam, signal, {}, true),
    getNextPageParam: (page) => page.nextCursor ?? undefined,
    enabled: presentation.data !== undefined,
  })
  const workflows =
    accessDenied(query.error) || presentation.data === undefined
      ? undefined
      : query.data?.pages.flatMap((page) => page.items)
  return (
    <>
      <nav aria-label="Breadcrumb" className="flex flex-wrap gap-2">
        <Link to="/">Teams</Link>
        <span aria-hidden="true">/</span>
        <Link
          to="/t/$team"
          params={{ team }}
          className="max-w-full min-w-0 break-words"
        >
          {team}
        </Link>
      </nav>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="min-w-0 text-2xl font-semibold break-words">
          {team} / {type}
        </h1>
        <Button
          variant="outline"
          disabled={query.isFetching || presentation.isFetching}
          onClick={() =>
            void Promise.all([query.refetch(), presentation.refetch()])
          }
        >
          Refresh workflows
        </Button>
      </div>
      <p role="status">
        {live.snapshot?.status === "live"
          ? "Live updates connected."
          : live.snapshot?.status === "error"
            ? "Live updates unavailable. Refresh workflows or retry."
            : "Connecting live updates..."}
      </p>
      <PresentationFilters config={config} />
      {live.snapshot?.error ? (
        <Button variant="outline" onClick={live.retry}>
          Retry live updates
        </Button>
      ) : null}
      {query.isPending ? <p role="status">Loading workflows...</p> : null}
      {query.error ? <ErrorNotice error={query.error} /> : null}
      {presentation.error ? <ErrorNotice error={presentation.error} /> : null}
      {workflows?.length === 0 ? (
        <p role="status">No workflows in this type.</p>
      ) : null}
      {workflows && workflows.length > 0 ? (
        <div
          role="region"
          aria-label="Workflow list"
          tabIndex={0}
          className="overflow-x-auto rounded-md border"
        >
          <table className="w-full text-left text-sm">
            <caption className="sr-only">
              Workflows, active and latest versions, and latest run status
            </caption>
            <thead className="border-b">
              <tr>
                <th scope="col" className="p-3">
                  Workflow
                </th>
                <th scope="col" className="p-3">
                  Active version
                </th>
                <th scope="col" className="p-3">
                  Latest version
                </th>
                <th scope="col" className="p-3">
                  Latest status
                </th>
                {columns.map((key) => (
                  <th key={key} scope="col" className="p-3">
                    {presentationLabel(config, key)}
                  </th>
                ))}
              </tr>
            </thead>
            {groupWorkflows(workflows, config).map((group) => (
              <tbody key={group.id}>
                {group.label ? (
                  <tr>
                    <th
                      scope="rowgroup"
                      colSpan={4 + columns.length}
                      className="bg-muted p-3 font-semibold"
                    >
                      {group.label}
                    </th>
                  </tr>
                ) : null}
                {group.items.map((workflow) => (
                  <tr key={workflow.id} className="border-b last:border-0">
                    <th scope="row" className="p-3 font-normal">
                      <span className="font-medium">{workflow.name}</span>
                      <p className="break-words text-muted-foreground">
                        {workflow.id}
                      </p>
                    </th>
                    <td className="p-3">
                      {workflow.activeVersion?.semVer ?? "No active version"}
                    </td>
                    <td className="p-3">{workflow.latestVersion.semVer}</td>
                    <td className="p-3">
                      <StatusBadge
                        status={workflow.latestRun?.status ?? "NeverRun"}
                      />
                      {workflow.latestRun ? (
                        <p className="mt-2 font-mono text-xs">
                          <Link
                            to="/runs/$id"
                            params={{ id: workflow.latestRun.id }}
                          >
                            {workflow.latestRun.id}
                          </Link>
                        </p>
                      ) : null}
                    </td>
                    {columns.map((key) => (
                      <td key={key} className="p-3">
                        {metadataValue(workflow, key) ?? "Not set"}
                      </td>
                    ))}
                  </tr>
                ))}
              </tbody>
            ))}
          </table>
        </div>
      ) : null}
      {query.hasNextPage ? (
        <Button
          variant="outline"
          disabled={query.isFetching}
          onClick={() => void query.fetchNextPage()}
        >
          {query.isFetchingNextPage
            ? "Loading more workflows..."
            : "Load more workflows"}
        </Button>
      ) : null}
    </>
  )
}
