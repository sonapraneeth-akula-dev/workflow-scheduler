import { lazy, Suspense, useId, useMemo, useState } from "react"
import { useQuery } from "@tanstack/react-query"
import { ErrorNotice } from "@/components/ErrorNotice"
import { formatTimestamp } from "@/lib/timestamps"
import { Button } from "@/components/ui/button"
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select"
import { ApiError } from "@/lib/api/client"
import type { ChartData } from "@/renderers/chart/chartData"
import {
  getResourcePage,
  getResourceSummary,
  type ResourceCursor,
  type ResourcePage,
  type ResourcePoint,
  type ResourceSummary,
} from "./resource-api"

const Plot = lazy(() =>
  import("@/renderers/chart/ChartReport").then((module) => ({
    default: module.Plot,
  }))
)
function decimal(value: number | null, unit = "", why?: string | null): string {
  return value === null
    ? `Unavailable${why ? `: ${why}` : ""}`
    : `${String(value)}${unit}`
}
function bytes(value: string | null, why?: string | null): string {
  return value === null
    ? `Unavailable${why ? `: ${why}` : ""}`
    : `${BigInt(value).toLocaleString("en")} B`
}
const identity = (summary: ResourceSummary) =>
  JSON.stringify([summary.taskKey, summary.attempt])
const denied = (error: unknown) =>
  error instanceof ApiError && [401, 403].includes(error.status)

export function ResourceCharts({ runId }: { runId: string }) {
  const id = useId()
  const [refresh, setRefresh] = useState(0)
  const [selection, setSelection] = useState<string | null>(null)
  const [cursors, setCursors] = useState<readonly ResourceCursor[]>([])
  const [scope, setScope] = useState<"task" | "machine">("task")
  const summaries = useQuery({
    queryKey: ["resource-summary", runId, refresh],
    queryFn: ({ signal }) => getResourceSummary(runId, signal),
    retry: false,
    gcTime: 0,
  })
  const selected =
    summaries.data?.find((summary) => identity(summary) === selection) ??
    summaries.data?.[0]
  const cursor = cursors.at(-1) ?? null
  const series = useQuery({
    queryKey: [
      "resource-series",
      runId,
      selected?.taskKey,
      selected?.attempt,
      cursor,
      refresh,
    ],
    enabled: selected !== undefined && summaries.isSuccess,
    queryFn: ({ signal }) => {
      if (!selected)
        throw new Error("Select a dispatched attempt before reading resources.")
      return getResourcePage(runId, selected, cursor, signal)
    },
    retry: false,
    gcTime: 0,
  })
  const forbidden = denied(summaries.error) || denied(series.error)
  const items = !summaries.error && !forbidden ? summaries.data : undefined
  const page = !series.error && !forbidden ? series.data : undefined
  return (
    <section aria-label="Run resource metrics" className="min-w-0 space-y-5">
      <h2 className="text-xl font-semibold">Resource metrics</h2>
      <p>
        Recorded attempt samples, not live estimates. Refresh to read newly
        committed data. Missing measurements retain their reasons and never
        become zero.
      </p>
      <Button
        variant="outline"
        disabled={summaries.isFetching || series.isFetching}
        onClick={() => {
          setCursors([])
          setRefresh((value) => value + 1)
        }}
      >
        Refresh resource metrics
      </Button>
      {summaries.isPending || summaries.isFetching ? (
        <p role="status">Loading resource summaries...</p>
      ) : null}
      {summaries.error ? <ErrorNotice error={summaries.error} /> : null}
      {series.error ? <ErrorNotice error={series.error} /> : null}
      {forbidden ? (
        <p>
          Previously loaded metrics are hidden because access is no longer
          authorized.
        </p>
      ) : null}
      {items ? (
        <>
          {items.length === 0 ? (
            <p>No dispatched attempts have resource metrics yet.</p>
          ) : (
            <>
              <SummaryTable items={items} />
              <div className="space-y-2">
                <label htmlFor={`${id}-attempt`}>Resource attempt</label>
                <NativeSelect
                  id={`${id}-attempt`}
                  className="max-w-full"
                  value={selected ? identity(selected) : ""}
                  onChange={(event) => {
                    setSelection(event.target.value)
                    setCursors([])
                  }}
                >
                  {items.map((item) => (
                    <NativeSelectOption
                      key={identity(item)}
                      value={identity(item)}
                    >
                      {item.taskKey} - attempt {item.attempt}
                    </NativeSelectOption>
                  ))}
                </NativeSelect>
              </div>
              {selected ? (
                <p className="break-all">
                  Agent: {selected.agentId ?? "Not recorded"};{" "}
                  {selected.sampleCount} recorded samples.
                </p>
              ) : null}
              <div className="space-y-2">
                <label htmlFor={`${id}-scope`}>Resource scope</label>
                <NativeSelect
                  id={`${id}-scope`}
                  className="max-w-full"
                  value={scope}
                  onChange={(event) =>
                    setScope(
                      event.target.value === "machine" ? "machine" : "task"
                    )
                  }
                >
                  <NativeSelectOption value="task">
                    Task process scope
                  </NativeSelectOption>
                  <NativeSelectOption value="machine">
                    Machine context
                  </NativeSelectOption>
                </NativeSelect>
              </div>
              <p>
                Task CPU: one core = 100%, so multithreaded work can exceed
                100%. Machine CPU: percentage of the whole machine. Windows task
                network counters cover the host process's transport bytes, not
                child processes; Linux cgroup counters include descendants and
                L3 overhead. Machine traffic is non-loopback interface context,
                not task-attributed traffic. Do not add these scopes.
              </p>
              {series.isFetching ? (
                <p role="status">Loading resource samples...</p>
              ) : null}
              <div
                className="flex flex-wrap gap-3"
                aria-label="Resource sample pages"
              >
                <Button
                  variant="outline"
                  disabled={cursors.length === 0 || series.isFetching}
                  onClick={() => setCursors((values) => values.slice(0, -1))}
                >
                  Newer samples
                </Button>
                <Button
                  variant="outline"
                  disabled={!page?.nextCursor || series.isFetching}
                  onClick={() => {
                    const last = page?.samples.at(-1)
                    if (page?.nextCursor && last)
                      setCursors((values) => [
                        ...values,
                        { token: page.nextCursor!, before: last.sequence },
                      ])
                  }}
                >
                  Older samples
                </Button>
              </div>
              {page ? <SampleSeries page={page} scope={scope} /> : null}
            </>
          )}
        </>
      ) : null}
    </section>
  )
}

function SummaryTable({ items }: { items: readonly ResourceSummary[] }) {
  return (
    <div
      className="overflow-x-auto rounded border"
      role="region"
      aria-label="Attempt resource summaries"
      tabIndex={0}
    >
      <table className="w-full text-left text-sm">
        <caption className="p-3 text-left">
          Task-attributed attempt summaries; bytes are exact integers. Averages
          use observed CPU time coverage only.
        </caption>
        <thead>
          <tr>
            {[
              "Task",
              "Attempt",
              "Samples",
              "Average CPU",
              "Maximum CPU",
              "Peak RAM",
              "Sent bytes",
              "Received bytes",
              "CPU coverage",
              "Network coverage",
            ].map((label) => (
              <th key={label} scope="col" className="p-2">
                {label}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {items.map((item) => (
            <tr key={identity(item)} className="border-t">
              <th scope="row" className="p-2 break-all">
                {item.taskKey}
              </th>
              {[
                String(item.attempt),
                item.sampleCount,
                decimal(item.averageCpuPercent, "%", item.cpuUnavailableReason),
                decimal(item.maxCpuPercent, "%", item.cpuUnavailableReason),
                bytes(item.peakRamBytes, item.memoryUnavailableReason),
                bytes(item.networkSentBytes, item.networkUnavailableReason),
                bytes(item.networkReceivedBytes, item.networkUnavailableReason),
                decimal(item.cpuObservedSeconds, " s"),
                decimal(item.networkObservedSeconds, " s"),
              ].map((value, index) => (
                <td key={index} className="p-2">
                  {value}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

export function resourcePlots(
  page: ResourcePage,
  scope: "task" | "machine"
): { title: string; data: ChartData }[] {
  const points = [...page.samples]
    .reverse()
    .map((sample) => sample.observation[scope])
  const x = points.map((_, index) => index + 1)
  return [
    {
      title: `${scope === "task" ? "Task" : "Machine"} CPU (%)`,
      data: {
        type: "line",
        xLabel: "Sample order (page)",
        yLabel: "CPU (%)",
        x,
        series: [
          {
            label: "CPU (%)",
            values: points.map((point) => point?.cpuPercent ?? null),
          },
        ],
      },
    },
    {
      title: `${scope === "task" ? "Task" : "Machine"} RAM (MiB)`,
      data: {
        type: "line",
        xLabel: "Sample order (page)",
        yLabel: "RAM (MiB)",
        x,
        series: [
          {
            label: "RAM (MiB)",
            values: points.map((point) =>
              point?.ramBytes === null || !point
                ? null
                : Number(point.ramBytes) / 1048576
            ),
          },
        ],
      },
    },
    {
      title: `${scope === "task" ? "Task" : "Machine"} network (B/s)`,
      data: {
        type: "line",
        xLabel: "Sample order (page)",
        yLabel: "Network rate (B/s)",
        x,
        series: [
          {
            label: "Sent (B/s)",
            values: points.map(
              (point) => point?.networkSentBytesPerSecond ?? null
            ),
          },
          {
            label: "Received (B/s)",
            values: points.map(
              (point) => point?.networkReceivedBytesPerSecond ?? null
            ),
          },
        ],
      },
    },
  ]
}

function SampleSeries({
  page,
  scope,
}: {
  page: ResourcePage
  scope: "task" | "machine"
}) {
  const plots = useMemo(() => resourcePlots(page, scope), [page, scope])
  if (page.samples.length === 0)
    return <p>No resource samples received for this attempt.</p>
  const samples = [...page.samples].reverse()
  const available = samples.some((sample) => sample.observation[scope] !== null)
  return (
    <div className="min-w-0 space-y-5">
      <p>
        Showing sample sequences {samples[0]!.sequence} to{" "}
        {samples.at(-1)!.sequence}, oldest to newest. At most 60 samples load
        per page. Chart X is sample order within this page, not elapsed time:
        UTC clocks may be corrected. Exact timestamps, counters, scopes and
        unavailable reasons remain in the table. RAM chart coordinates are
        approximate MiB; the table retains exact bytes. Nulls remain gaps.
      </p>
      {available ? (
        <Suspense fallback={<p role="status">Loading resource charts...</p>}>
          {plots.map((plot) => (
            <Plot key={plot.title} {...plot} />
          ))}
        </Suspense>
      ) : (
        <p>
          Machine observations are unavailable for this page; see the recorded
          reasons below.
        </p>
      )}
      <div
        className="overflow-x-auto rounded border"
        role="region"
        aria-label="Exact resource samples"
        tabIndex={0}
      >
        <table className="w-full text-left text-sm">
          <caption className="p-3 text-left">
            Exact {scope} resource samples
          </caption>
          <thead>
            <tr>
              {[
                "Sequence",
                "Time (local)",
                "Interval",
                "CPU",
                "RAM",
                "Sent counter",
                "Received counter",
                "Sent delta",
                "Received delta",
                "Sent rate",
                "Received rate",
                "Scopes",
              ].map((label) => (
                <th key={label} scope="col" className="p-2">
                  {label}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {samples.map((sample) => (
              <SampleRow
                key={sample.sequence}
                sequence={sample.sequence}
                point={sample.observation[scope]}
                why={
                  scope === "machine"
                    ? sample.observation.machineUnavailableReason
                    : null
                }
              />
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function SampleRow({
  sequence,
  point,
  why,
}: {
  sequence: string
  point: ResourcePoint | null
  why: string | null
}) {
  return (
    <tr className="border-t">
      <th scope="row" className="p-2">
        {sequence}
      </th>
      {point ? (
        [
          <time key="at" dateTime={point.atUtc} title={point.atUtc}>
            {formatTimestamp(point.atUtc)}
          </time>,
          decimal(point.intervalSeconds, " s", "No interval recorded."),
          decimal(point.cpuPercent, "%", point.cpuUnavailableReason),
          bytes(point.ramBytes, point.memoryUnavailableReason),
          bytes(point.networkSentBytes, point.networkUnavailableReason),
          bytes(point.networkReceivedBytes, point.networkUnavailableReason),
          bytes(
            point.networkSentDeltaBytes,
            point.networkRateUnavailableReason
          ),
          bytes(
            point.networkReceivedDeltaBytes,
            point.networkRateUnavailableReason
          ),
          decimal(
            point.networkSentBytesPerSecond,
            " B/s",
            point.networkRateUnavailableReason
          ),
          decimal(
            point.networkReceivedBytesPerSecond,
            " B/s",
            point.networkRateUnavailableReason
          ),
          `${point.cpuScope}; ${point.memoryScope}; ${point.networkScope}`,
        ].map((value, index) => (
          <td key={index} className="p-2">
            {value}
          </td>
        ))
      ) : (
        <td colSpan={11} className="p-2">
          Unavailable: {why}
        </td>
      )}
    </tr>
  )
}
