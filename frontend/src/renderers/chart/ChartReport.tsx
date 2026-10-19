import { useEffect, useMemo, useRef, useState } from "react"
import uPlot from "uplot"
import "uplot/dist/uPlot.min.css"
import { ErrorNotice } from "@/components/ErrorNotice"
import { BlobText } from "../BlobText"
import { VirtualTable } from "../table/VirtualTable"
import type { ReportRendererProps } from "../types"
import { parseChart, type ChartData } from "./chartData"

const colors = [
  "#2563eb",
  "#dc2626",
  "#16a34a",
  "#9333ea",
  "#ea580c",
  "#0891b2",
]

export function Plot({
  data,
  title,
  xAxis = "number",
}: {
  data: ChartData
  title: string
  xAxis?: "number" | "utc-date"
}) {
  const container = useRef<HTMLDivElement>(null)
  const [error, setError] = useState<unknown>(null)
  const table = useMemo(
    () => ({
      columns: [
        xAxis === "utc-date" ? "UTC creation day" : "X",
        ...data.series.map((series) => series.label),
      ],
      rows: data.x.map((x, index) => [
        xAxis === "utc-date"
          ? new Date(Number(x) * 1000).toISOString().slice(0, 10)
          : String(x),
        ...data.series.map((series) =>
          series.values[index] === null ? "" : String(series.values[index])
        ),
      ]),
    }),
    [data, xAxis]
  )
  useEffect(() => {
    const target = container.current!
    const foreground = () => getComputedStyle(target).color
    const border = () =>
      getComputedStyle(document.documentElement).getPropertyValue("--border")
    let plot: uPlot | undefined
    let resize: ResizeObserver | undefined
    let theme: MutationObserver | undefined
    try {
      const bars = uPlot.paths.bars
      if (data.type === "bar" && !bars)
        throw new Error("Bar chart support is unavailable.")
      const path =
        data.type === "bar"
          ? bars!({
              disp: {
                x0: {
                  unit: 1,
                  values: (_, series) =>
                    data.x.map(
                      (_, index) =>
                        index - 0.35 + ((series - 1) * 0.7) / data.series.length
                    ),
                },
                size: { unit: 1, values: () => [0.7 / data.series.length] },
              },
            })
          : undefined
      const values: uPlot.AlignedData = [
        data.x.map((value, index) =>
          data.type === "line" ? Number(value) : index
        ),
        ...data.series.map((series) => series.values),
      ]
      plot = new uPlot(
        {
          width: Math.max(1, target.clientWidth),
          height: 320,
          ...(xAxis === "utc-date"
            ? {
                tzDate: (timestamp: number) =>
                  uPlot.tzDate(new Date(timestamp * 1000), "Etc/UTC"),
              }
            : {}),
          scales: {
            x: {
              time: xAxis === "utc-date",
              range:
                data.type === "bar" ? [-0.5, data.x.length - 0.5] : undefined,
            },
            y: {
              range:
                data.type === "bar"
                  ? (_: uPlot, min: number, max: number): [number, number] => {
                      const low = Math.min(0, min)
                      const high = Math.max(0, max)
                      return low === high ? [-1, 1] : [low, high]
                    }
                  : undefined,
            },
          },
          legend: { show: false },
          series: [
            {},
            ...data.series.map((series, index) => ({
              label: series.label,
              stroke: colors[index % colors.length],
              fill:
                data.type === "bar" ? colors[index % colors.length] : undefined,
              paths: path,
              points: { show: xAxis === "utc-date" },
              spanGaps: false,
            })),
          ],
          axes: [
            {
              stroke: foreground,
              grid: { stroke: border },
              ...(xAxis === "utc-date"
                ? {
                    values: (_: uPlot, splits: number[]) =>
                      splits.map((value) =>
                        new Date(value * 1000).toISOString().slice(0, 10)
                      ),
                    size: 70,
                    space: 100,
                  }
                : data.type === "bar"
                  ? {
                      values: (_: uPlot, splits: number[]) =>
                        splits.map((value) =>
                          Number.isInteger(value) &&
                          value >= 0 &&
                          value < data.x.length
                            ? String(data.x[value])
                            : ""
                        ),
                    }
                  : {}),
            },
            { stroke: foreground, grid: { stroke: border } },
          ],
        },
        values,
        target
      )
      const current = plot
      resize = new ResizeObserver(() =>
        current.setSize({ width: Math.max(1, target.clientWidth), height: 320 })
      )
      resize.observe(target)
      theme = new MutationObserver(() => current.redraw(true))
      theme.observe(document.documentElement, {
        attributes: true,
        attributeFilter: ["class"],
      })
    } catch (failure) {
      // A native canvas allocation can fail only after the component commits.
      // eslint-disable-next-line react-hooks/set-state-in-effect
      setError(failure)
    }
    return () => {
      resize?.disconnect()
      theme?.disconnect()
      plot?.destroy()
      target.replaceChildren()
    }
  }, [data, xAxis])
  return (
    <section className="min-w-0 space-y-3" aria-label={`${title} chart`}>
      {error ? <ErrorNotice error={error} /> : null}
      <div
        ref={container}
        role="img"
        aria-label={`${title}: ${data.type} chart`}
      />
      <ul className="flex flex-wrap gap-4" aria-label="Chart series">
        {data.series.map((series, index) => (
          <li key={index} className="break-words">
            <span
              aria-hidden="true"
              className="mr-2 inline-block size-3"
              style={{ backgroundColor: colors[index % colors.length] }}
            />
            {series.label}
          </li>
        ))}
      </ul>
      <details>
        <summary className="cursor-pointer">Chart data table</summary>
        <VirtualTable {...table} title={`${title} data`} />
      </details>
    </section>
  )
}

function ChartContent({ text, title }: { text: string; title: string }) {
  let data: ChartData
  try {
    data = parseChart(text)
  } catch (error) {
    return <ErrorNotice error={error} />
  }
  return <Plot data={data} title={title} />
}

export default function ChartReport({ content, title }: ReportRendererProps) {
  return (
    <BlobText content={content}>
      {(text) => <ChartContent key={text} text={text} title={title} />}
    </BlobText>
  )
}
