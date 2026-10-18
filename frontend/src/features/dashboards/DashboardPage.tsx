import { useQuery } from "@tanstack/react-query"
import { Link } from "@tanstack/react-router"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
import { Plot } from "@/renderers/chart/ChartReport"
import type { ChartData } from "@/renderers/chart/chartData"
import { trendWindowSchema, type TrendWindow } from "@/features/series/api"
import { encodeDashboardId, getDashboard, type DashboardResource } from "./api"

const numbers = new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 })
const percent = new Intl.NumberFormat(undefined, {
  style: "percent",
  maximumFractionDigits: 1,
})
const rankings = [
  { key: "slowest", title: "Slowest nodes", metric: "p95Ms", unit: "ms" },
  {
    key: "failureRate",
    title: "Highest failure rate",
    metric: "failureRate",
    unit: "%",
  },
  { key: "cpu", title: "Highest CPU", metric: "cpuMaxPercent", unit: "%" },
  { key: "ram", title: "Highest RAM", metric: "ramPeakMb", unit: "MiB" },
] as const

export function DashboardPage({
  scope,
  id,
  window = "30d",
  top = 10,
  onChange,
}: DashboardResource & {
  window?: TrendWindow
  top?: number
  onChange: (search: { window: TrendWindow; top: number }) => void
}) {
  const query = useQuery({
    queryKey: ["dashboards", scope, id, window, top],
    queryFn: ({ signal }) => getDashboard(scope, id, window, top, signal),
    retry: false,
    staleTime: 0,
  })
  // This scoped read itself checks current Viewer authority, including machine analytics without inventory access.
  const data = query.isSuccess && !query.isFetching ? query.data : undefined
  return (
    <section aria-label="Metric dashboard" className="min-w-0 space-y-5">
      <h1 className="text-2xl font-semibold capitalize">{scope} dashboard</h1>
      <p className="text-sm break-all text-muted-foreground">Scope ID: {id}</p>
      <div className="flex flex-wrap items-end gap-3">
        <label className="flex flex-col gap-1">
          Dashboard window
          <select
            value={window}
            onChange={(event) =>
              onChange({
                window: trendWindowSchema.parse(event.currentTarget.value),
                top,
              })
            }
            className="rounded border bg-background p-2"
          >
            <option value="7d">Last 7 days</option>
            <option value="30d">Last 30 days</option>
            <option value="90d">Last 90 days</option>
          </select>
        </label>
        <label className="flex flex-col gap-1">
          Top rankings
          <select
            value={top}
            onChange={(event) =>
              onChange({ window, top: Number(event.currentTarget.value) })
            }
            className="rounded border bg-background p-2"
          >
            {[...new Set([5, 10, 25, 50, top])]
              .sort((a, b) => a - b)
              .map((n) => (
                <option key={n} value={n}>
                  {n}
                </option>
              ))}
          </select>
        </label>
        <Button
          variant="outline"
          disabled={query.isFetching}
          onClick={() => void query.refetch()}
        >
          Refresh dashboard
        </Button>
      </div>
      <p className="text-sm text-muted-foreground">
        UTC creation days, including today; completed runs only.{" "}
        {scope === "machine"
          ? "Observations are terminal task attempts on this exact machine, restricted to your currently permitted workflow types."
          : "Observations are terminal node occurrences."}{" "}
        Failure rate includes hard failure, timeout and cancellation, not soft
        failures. Missing measurements are not recorded; measured zero is
        retained. CPU and RAM are maxima, including failed retries. Latest
        contribution is not a percentile exemplar.
      </p>
      {query.isFetching ? (
        <p role="status">Checking dashboard access and loading rankings...</p>
      ) : null}
      {query.error ? <ErrorNotice error={query.error} /> : null}
      {data ? (
        <>
          <p role="status">
            {data.from} through{" "}
            {new Date(Date.parse(data.to) - 86400000)
              .toISOString()
              .slice(0, 10)}{" "}
            (UTC). Top {data.top} per ranking.
          </p>
          {data.rankings.failureRate.length === 0 ? (
            <p role="status">No completed observations in this window.</p>
          ) : null}
          <p className="text-sm text-muted-foreground">
            Node links open workflow runs filtered to this UTC window, across
            schedules and machines. Latest contribution opens a run from this
            exact dashboard scope.
          </p>
          {rankings.map(({ key, title, metric, unit }) => {
            const rows = data.rankings[key]
            const chart: ChartData = {
              type: "bar",
              x: rows.map((_, index) => String(index + 1)),
              series: [
                {
                  label: `${title} (${unit})`,
                  values: rows.map((row) =>
                    key === "failureRate" ? row.failureRate * 100 : row[metric]
                  ),
                },
              ],
            }
            return (
              <section
                key={key}
                aria-label={title}
                className="min-w-0 space-y-3"
              >
                <h2 className="text-xl font-semibold">{title}</h2>
                {rows.length === 0 ? (
                  <p>No recorded observations for this ranking.</p>
                ) : (
                  <>
                    <Plot data={chart} title={`${title} (${unit})`} />
                    <div
                      role="region"
                      aria-label={`Scrollable ${title.toLowerCase()} table`}
                      tabIndex={0}
                      className="overflow-x-auto rounded border"
                    >
                      <table className="w-full text-left text-sm">
                        <caption className="p-3 text-left font-medium">
                          {title}: values and run drill-down
                        </caption>
                        <thead className="border-b">
                          <tr>
                            {[
                              "Rank",
                              "Workflow / node",
                              `${title} (${unit})`,
                              "Observations",
                              "Hard / soft failures",
                              "Measured durations",
                              "Drill-down",
                            ].map((label) => (
                              <th scope="col" key={label} className="p-3">
                                {label}
                              </th>
                            ))}
                          </tr>
                        </thead>
                        <tbody>
                          {rows.map((row, index) => (
                            <tr
                              key={`${row.workflowId}/${row.nodeKey}`}
                              className="border-b last:border-0"
                            >
                              <td className="p-3">{index + 1}</td>
                              <th scope="row" className="p-3 font-normal">
                                <Link
                                  to="/runs"
                                  search={{
                                    workflowId: row.workflowId,
                                    from: `${data.from}T00:00:00Z`,
                                    to: `${data.to}T00:00:00Z`,
                                  }}
                                  className="block font-medium underline"
                                >
                                  {row.nodeKey}
                                </Link>
                                <span className="break-all text-muted-foreground">
                                  {row.workflowId}
                                </span>
                              </th>
                              <td className="p-3 whitespace-nowrap">
                                {key === "failureRate"
                                  ? percent.format(row.failureRate)
                                  : `${numbers.format(row[metric]!)} ${unit}`}
                              </td>
                              <td className="p-3">
                                {numbers.format(row.observations)}
                              </td>
                              <td className="p-3">
                                {numbers.format(row.failures)} /{" "}
                                {numbers.format(row.softFailures)}
                              </td>
                              <td className="p-3">
                                {numbers.format(row.durationCount)}
                              </td>
                              <td className="space-y-2 p-3">
                                <Link
                                  to="/runs/$id"
                                  params={{ id: row.latestRunId }}
                                  className="block underline"
                                >
                                  Latest contribution
                                </Link>
                                <Link
                                  to="/dashboards/$scope/$id"
                                  params={{
                                    scope: "node",
                                    id: encodeDashboardId(
                                      `${row.workflowId}/${row.nodeKey}`
                                    ),
                                  }}
                                  search={{ window, top }}
                                  className="block underline"
                                >
                                  Node dashboard
                                </Link>
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  </>
                )}
              </section>
            )
          })}
        </>
      ) : null}
    </section>
  )
}
