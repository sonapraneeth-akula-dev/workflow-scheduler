import { useInfiniteQuery, useQuery } from "@tanstack/react-query"
import { Link } from "@tanstack/react-router"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { NativeSelect } from "@/components/ui/native-select"
import { getTeams, getTypes } from "@/features/catalog/api"
import { presentationName } from "@/features/catalog/usePresentation"
import { RunNowButton } from "@/features/series/RunNowButton"
import { formatTimestamp } from "@/lib/timestamps"
import { durationSeconds } from "./schedule-draft"
import {
  getRecurringJobs,
  type RecurringJob,
  type RecurringJobsSearch,
  type ScheduleCursor,
} from "./recurring-jobs-api"

export function RecurringJobs({
  search,
  onChange,
}: {
  search: RecurringJobsSearch
  onChange: (next: RecurringJobsSearch) => void
}) {
  const teams = useQuery({
    queryKey: ["catalog", "teams"],
    queryFn: ({ signal }) => getTeams(signal),
  })
  const types = useQuery({
    queryKey: ["catalog", "team", search.team, "filter-types"],
    enabled: search.team !== undefined,
    queryFn: ({ signal }) => {
      if (!search.team) throw new Error("Choose a team to load its types.")
      return getTypes(search.team, signal)
    },
  })
  const query = useInfiniteQuery({
    queryKey: ["recurring-jobs", search.team, search.type],
    initialPageParam: null as ScheduleCursor,
    queryFn: ({ pageParam, signal }) =>
      getRecurringJobs(search, pageParam, signal),
    getNextPageParam: (page): ScheduleCursor | undefined =>
      page.nextCursor
        ? { cursor: page.nextCursor, afterId: page.items.at(-1)!.id }
        : undefined,
    refetchInterval: 30_000,
  })
  const unavailable = Boolean(query.error || teams.error || types.error)
  const jobs = unavailable
    ? undefined
    : query.data?.pages.flatMap((page) => page.items)
  const groups = new Map<string, Map<string, Map<string, RecurringJob[]>>>()
  for (const job of jobs ?? []) {
    const [team, type] = job.workflowId.split("/")
    if (!team || !type) throw new Error("A recurring job has no team or type.")
    if (!groups.has(team)) groups.set(team, new Map())
    const group = groups.get(team)!
    if (!group.has(type)) group.set(type, new Map())
    const workflows = group.get(type)!
    if (!workflows.has(job.workflowId)) workflows.set(job.workflowId, [])
    workflows.get(job.workflowId)!.push(job)
  }
  const teamConfig = teams.error ? undefined : teams.data
  const selectedConfig =
    teamConfig?.find((team) => team.team === search.team)?.presentation ?? null
  return (
    <div className="min-w-0 space-y-4">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="text-2xl font-semibold">Recurring jobs</h1>
          <p className="text-sm text-muted-foreground">
            Scheduled jobs in tables grouped by team, workflow type and
            workflow. Includes active and paused Code and User schedules you can
            view.
          </p>
        </div>
        <div className="flex flex-wrap items-end gap-3">
          <label className="flex min-w-0 flex-col gap-1 text-sm">
            Team
            <NativeSelect
              value={search.team ?? ""}
              onChange={(event) =>
                onChange({
                  team: event.target.value || undefined,
                  type: undefined,
                })
              }
            >
              <option value="">All teams</option>
              {search.team &&
              !teamConfig?.some((team) => team.team === search.team) ? (
                <option value={search.team}>{search.team} (unavailable)</option>
              ) : null}
              {teamConfig?.map((team) => (
                <option key={team.team} value={team.team}>
                  {presentationName(team.presentation, team.team)}
                </option>
              ))}
            </NativeSelect>
          </label>
          <label className="flex min-w-0 flex-col gap-1 text-sm">
            Workflow type
            <NativeSelect
              value={search.type ?? ""}
              disabled={!search.team || types.isPending || Boolean(types.error)}
              onChange={(event) =>
                onChange({ ...search, type: event.target.value || undefined })
              }
            >
              <option value="">All workflow types</option>
              {search.type &&
              !types.data?.some((type) => type.name === search.type) ? (
                <option value={search.type}>{search.type} (unavailable)</option>
              ) : null}
              {!types.error
                ? types.data?.map((type) => (
                    <option key={type.name} value={type.name}>
                      {presentationName(
                        selectedConfig,
                        type.name,
                        `${type.team}/${type.name}`
                      )}
                    </option>
                  ))
                : null}
            </NativeSelect>
          </label>
          <Button
            variant="outline"
            disabled={query.isFetching || teams.isFetching || types.isFetching}
            onClick={() =>
              void Promise.all([
                query.refetch(),
                teams.refetch(),
                ...(search.team ? [types.refetch()] : []),
              ])
            }
          >
            Refresh recurring jobs
          </Button>
        </div>
      </div>
      <p className="text-sm text-muted-foreground">
        Run now requires Operator permission and starts one manual run using the
        schedule's saved variables and version selection. It does not resume a
        paused job or move its next automatic fire. This list refreshes every 30
        seconds.
      </p>
      {query.isPending ? <p role="status">Loading recurring jobs...</p> : null}
      {query.error ? <ErrorNotice error={query.error} /> : null}
      {teams.error ? <ErrorNotice error={teams.error} /> : null}
      {types.error ? <ErrorNotice error={types.error} /> : null}
      {jobs?.length === 0 ? (
        <p role="status">No recurring jobs match the selected team and type.</p>
      ) : jobs ? (
        <p className="text-sm text-muted-foreground">
          Showing {jobs.length} loaded recurring{" "}
          {jobs.length === 1 ? "job" : "jobs"}.
          {query.hasNextPage ? " Load more to see the remaining jobs." : ""}
        </p>
      ) : null}
      {[...groups]
        .sort(([left], [right]) => left.localeCompare(right))
        .map(([team, types]) => {
          const config =
            teamConfig?.find((entry) => entry.team === team)?.presentation ??
            null
          const teamName = presentationName(config, team)
          return (
            <section
              key={team}
              aria-label={`${teamName} recurring jobs`}
              className="min-w-0 space-y-4 border bg-card p-3 sm:p-4"
            >
              <h2 className="text-xl font-semibold break-words">{teamName}</h2>
              {[...types]
                .sort(([left], [right]) => left.localeCompare(right))
                .map(([type, workflows]) => {
                  const typeName = presentationName(
                    config,
                    type,
                    `${team}/${type}`
                  )
                  return (
                    <section
                      key={type}
                      aria-label={`${teamName} / ${typeName}`}
                      className="min-w-0 space-y-3"
                    >
                      <h3 className="text-lg font-semibold break-words">
                        {typeName}
                      </h3>
                      <div
                        role="region"
                        aria-label={`${teamName} / ${typeName} recurring jobs table`}
                        tabIndex={0}
                        className="relative overflow-auto border"
                      >
                        <table className="w-full min-w-[64rem] text-left text-sm">
                          <caption className="sr-only">
                            {teamName} / {typeName} recurring jobs grouped by
                            workflow
                          </caption>
                          <thead className="border-b bg-secondary text-secondary-foreground">
                            <tr>
                              {[
                                "Job",
                                "Status",
                                "Source",
                                "Recurrence",
                                "Schedule timezone",
                                "Next fire (local time)",
                                "Version selection",
                                "Actions",
                              ].map((label) => (
                                <th
                                  key={label}
                                  scope="col"
                                  className="p-3 font-semibold"
                                >
                                  {label}
                                </th>
                              ))}
                            </tr>
                          </thead>
                          {[...workflows]
                            .sort(([left], [right]) =>
                              left.localeCompare(right)
                            )
                            .map(([workflowId, entries]) => {
                              const name = presentationName(
                                config,
                                workflowId.split("/")[2]!,
                                workflowId
                              )
                              return (
                                <tbody
                                  key={workflowId}
                                  aria-label={name}
                                  data-workflow-id={workflowId}
                                >
                                  <tr className="border-t-4 border-b border-t-primary bg-secondary text-secondary-foreground">
                                    <th
                                      scope="rowgroup"
                                      colSpan={8}
                                      className="p-4"
                                    >
                                      <div className="flex flex-wrap items-start justify-between gap-3">
                                        <div className="min-w-0 space-y-1">
                                          <p className="text-xs font-medium tracking-wide uppercase">
                                            Workflow
                                          </p>
                                          <h4 className="text-lg font-semibold break-words">
                                            {name}
                                          </h4>
                                          <p className="font-mono text-xs font-normal break-all">
                                            {workflowId}
                                          </p>
                                        </div>
                                        <Badge
                                          variant="outline"
                                          className="bg-background"
                                        >
                                          {entries.length}{" "}
                                          {entries.length === 1
                                            ? "job"
                                            : "jobs"}{" "}
                                          shown
                                        </Badge>
                                      </div>
                                    </th>
                                  </tr>
                                  {entries.map((job) => (
                                    <tr
                                      key={job.id}
                                      data-schedule-id={job.id}
                                      className="border-b bg-background align-top last:border-b-0"
                                    >
                                      <th
                                        scope="row"
                                        className="p-3 font-medium break-words"
                                      >
                                        {job.name}
                                      </th>
                                      <td className="p-3">
                                        <Badge variant="secondary">
                                          {job.paused ? "Paused" : "Active"}
                                        </Badge>
                                      </td>
                                      <td className="p-3">
                                        <Badge variant="outline">
                                          {job.origin === "Code"
                                            ? "Code schedule"
                                            : "UI schedule"}
                                        </Badge>
                                      </td>
                                      <td className="p-3 break-all">
                                        {job.interval
                                          ? `Every ${durationSeconds(job.interval)} seconds`
                                          : `Cron: ${job.cron}`}
                                      </td>
                                      <td className="p-3 break-all">
                                        {job.timezone}
                                      </td>
                                      <td className="p-3">
                                        {job.paused ? (
                                          "Paused; no automatic fires"
                                        ) : job.nextFireAt ? (
                                          <time
                                            dateTime={job.nextFireAt}
                                            title={job.nextFireAt}
                                          >
                                            {formatTimestamp(job.nextFireAt)}
                                          </time>
                                        ) : (
                                          "Not scheduled"
                                        )}
                                      </td>
                                      <td className="p-3 break-words">
                                        {job.versionPin ?? "Active version"}
                                      </td>
                                      <td className="p-3">
                                        <div className="space-y-3">
                                          <div className="flex flex-wrap gap-3">
                                            <Link
                                              to="/schedules/$id"
                                              params={{ id: job.id }}
                                              className="underline"
                                            >
                                              Schedule details
                                            </Link>
                                            <Link
                                              to="/series/$id"
                                              params={{ id: job.seriesId }}
                                              className="underline"
                                            >
                                              Series history
                                            </Link>
                                          </div>
                                          <RunNowButton
                                            scheduleId={job.id}
                                            seriesId={job.seriesId}
                                            disabled={
                                              query.isFetching || unavailable
                                            }
                                          />
                                        </div>
                                      </td>
                                    </tr>
                                  ))}
                                </tbody>
                              )
                            })}
                        </table>
                      </div>
                    </section>
                  )
                })}
            </section>
          )
        })}
      {jobs && query.hasNextPage ? (
        <Button
          variant="outline"
          disabled={query.isFetching}
          onClick={() => void query.fetchNextPage()}
        >
          Load more recurring jobs
        </Button>
      ) : null}
    </div>
  )
}
