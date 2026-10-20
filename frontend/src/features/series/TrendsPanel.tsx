import { useEffect, useMemo } from "react"
import { useQuery, useQueryClient } from "@tanstack/react-query"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
import { NativeSelect } from "@/components/ui/native-select"
import { accessDenied } from "@/features/catalog/api"
import { usePermissions } from "@/lib/auth/usePermissions"
import { Plot } from "@/renderers/chart/ChartReport"
import type { ChartData } from "@/renderers/chart/chartData"
import { getTrends, trendWindowSchema, type TrendWindow } from "./api"

const numbers = new Intl.NumberFormat(undefined, { maximumFractionDigits: 2 })
const percentages = new Intl.NumberFormat(undefined, {
  style: "percent",
  maximumFractionDigits: 1,
})
function percent(value: number | null) {
  return value === null ? "Not recorded" : percentages.format(value)
}
function duration(value: number | null) {
  return value === null ? "Not recorded" : `${numbers.format(value)} ms`
}

export function TrendsPanel({
  seriesId,
  window = "30d",
  onWindowChange,
}: {
  seriesId: string
  window?: TrendWindow
  onWindowChange: (window: TrendWindow) => void
}) {
  const permissions = usePermissions({ scope: "series", id: seriesId })
  const permitted = permissions.can("Viewer")
  // Revalidation hides data without toggling enabled and automatically retrying a denied read.
  const readable =
    permissions.isSuccess &&
    permissions.data.scope === "series" &&
    permissions.data.id === seriesId &&
    permissions.data.viewer
  const client = useQueryClient()
  const query = useQuery({
    queryKey: ["series-trends", seriesId, window],
    queryFn: ({ signal }) => getTrends(seriesId, window, signal),
    enabled: readable,
    retry: false,
  })
  useEffect(() => {
    if (accessDenied(query.error))
      void client.invalidateQueries({
        queryKey: ["permissions", "series", seriesId],
      })
  }, [client, query.error, seriesId])
  const data = permitted && !query.error ? query.data : undefined
  const charts = useMemo(() => {
    if (!data) return undefined
    const x = data.days.map((day) => day.day)
    const success: ChartData = {
      type: "bar",
      xLabel: "UTC creation day",
      yLabel: "Success rate (%)",
      x,
      series: [
        {
          label: "Success rate (%)",
          values: data.days.map((day) =>
            day.metrics.successRate === null
              ? null
              : day.metrics.successRate * 100
          ),
        },
      ],
    }
    const timing: ChartData = {
      type: "bar",
      xLabel: "UTC creation day",
      yLabel: "Duration (ms)",
      x,
      series: [
        {
          label: "p50 (ms)",
          values: data.days.map((day) => day.metrics.p50Ms),
        },
        {
          label: "p95 (ms)",
          values: data.days.map((day) => day.metrics.p95Ms),
        },
      ],
    }
    return { success, timing }
  }, [data])
  return (
    <section aria-label="Series trends" className="min-w-0 space-y-4">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <h2 className="text-xl font-semibold">Trends</h2>
        <label className="flex flex-col gap-1">
          Trend window
          <NativeSelect
            value={window}
            onChange={(event) =>
              onWindowChange(trendWindowSchema.parse(event.currentTarget.value))
            }
          >
            <option value="7d">Last 7 days</option>
            <option value="30d">Last 30 days</option>
            <option value="90d">Last 90 days</option>
          </NativeSelect>
        </label>
        <Button
          variant="outline"
          disabled={!permitted || query.isFetching}
          onClick={() => void query.refetch()}
        >
          Refresh trends
        </Button>
      </div>
      <p className="text-sm text-muted-foreground">
        UTC creation days, including today. Completed runs only; failures
        include aborted and timed-out runs. Streak follows creation order within
        this window, ignoring active runs and missed/skipped fires. Missing
        measurements remain unrecorded.
      </p>
      {permissions.error ? <ErrorNotice error={permissions.error} /> : null}
      {permissions.isPending || permissions.isFetching ? (
        <p role="status">Checking series permissions...</p>
      ) : !permitted && !permissions.error ? (
        <p role="alert">Series Viewer permission is required to view trends.</p>
      ) : null}
      {permitted && query.isPending ? (
        <p role="status">Loading trends...</p>
      ) : null}
      {query.error ? <ErrorNotice error={query.error} /> : null}
      {data && charts ? (
        <>
          <p role="status">
            {query.isFetching ? "Refreshing trends. " : ""}
            {data.from} through {data.days.at(-1)!.day} (UTC).
          </p>
          <dl className="grid min-w-0 gap-3 sm:grid-cols-2 lg:grid-cols-4">
            {Object.entries({
              "Completed runs": numbers.format(data.totals.runs),
              "Succeeded runs": numbers.format(data.totals.succeeded),
              "Failed runs": numbers.format(data.totals.failed),
              "Success rate": percent(data.totals.successRate),
              "p50 duration": duration(data.totals.p50Ms),
              "p95 duration": duration(data.totals.p95Ms),
              "Measured durations": numbers.format(data.totals.durationCount),
              "Current streak":
                data.streak.outcome === null
                  ? "No completed runs"
                  : `${numbers.format(data.streak.runs)} ${data.streak.outcome === "Succeeded" ? "succeeded" : "failed"}`,
            }).map(([label, value]) => (
              <div key={label} className="min-w-0 rounded border p-3">
                <dt className="text-sm text-muted-foreground">{label}</dt>
                <dd className="font-medium break-words">{value}</dd>
              </div>
            ))}
          </dl>
          {data.totals.runs === 0 ? (
            <p role="status">No completed runs in this window.</p>
          ) : (
            <div className="grid min-w-0 gap-6 xl:grid-cols-2">
              <Plot data={charts.success} title="Daily success rate (%)" />
              <Plot data={charts.timing} title="Daily duration (ms)" />
            </div>
          )}
          <div
            role="region"
            aria-label="Scrollable daily trends table"
            tabIndex={0}
            className="overflow-x-auto rounded border"
          >
            <table className="w-full text-left text-sm">
              <caption className="p-3 text-left font-medium">
                Daily series trends (UTC), tabular chart alternative
              </caption>
              <thead className="border-b">
                <tr>
                  {[
                    "Day (UTC)",
                    "Completed",
                    "Succeeded",
                    "Failed",
                    "Success rate",
                    "Measured durations",
                    "p50 duration",
                    "p95 duration",
                  ].map((label) => (
                    <th scope="col" key={label} className="p-3">
                      {label}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {data.days.map(({ day, metrics }) => (
                  <tr key={day} className="border-b last:border-0">
                    <th
                      scope="row"
                      className="p-3 font-normal whitespace-nowrap"
                    >
                      <time dateTime={day}>{day}</time>
                    </th>
                    <td className="p-3">{numbers.format(metrics.runs)}</td>
                    <td className="p-3">{numbers.format(metrics.succeeded)}</td>
                    <td className="p-3">{numbers.format(metrics.failed)}</td>
                    <td className="p-3">{percent(metrics.successRate)}</td>
                    <td className="p-3">
                      {numbers.format(metrics.durationCount)}
                    </td>
                    <td className="p-3 whitespace-nowrap">
                      {duration(metrics.p50Ms)}
                    </td>
                    <td className="p-3 whitespace-nowrap">
                      {duration(metrics.p95Ms)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      ) : null}
    </section>
  )
}
