import { useEffect, useId, useMemo, useRef, useState } from "react"
import uPlot from "uplot"
import "uplot/dist/uPlot.min.css"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
import { BlobText } from "../BlobText"
import { VirtualTable } from "../table/VirtualTable"
import type { ReportRendererProps } from "../types"
import { parseChart, type ChartData } from "./chartData"
import { createChartPng } from "./chartExport"

const tickNumber = new Intl.NumberFormat("en", {
  notation: "compact",
  maximumFractionDigits: 1,
})
const seriesColor = (index: number) => `var(--chart-${(index % 5) + 1})`

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
  const chart = useRef<uPlot | null>(null)
  const tooltip = useRef<HTMLDivElement>(null)
  const xValue = useRef<HTMLSpanElement>(null)
  const yValues = useRef<(HTMLSpanElement | null)[]>([])
  const selectedIndex = useRef<number | null>(null)
  const help = useId()
  const [error, setError] = useState<unknown>(null)
  const [exportError, setExportError] = useState<unknown>(null)
  const [exporting, setExporting] = useState(false)
  const [showGridlines, setShowGridlines] = useState(true)
  const xLabel =
    data.xLabel ??
    (xAxis === "utc-date"
      ? "UTC creation day"
      : data.type === "bar"
        ? "Category"
        : "X")
  const yLabel =
    data.yLabel ?? (data.series.length === 1 ? data.series[0]!.label : "Value")
  const table = useMemo(
    () => ({
      columns: [xLabel, ...data.series.map((series) => series.label)],
      rows: data.x.map((x, index) => [
        xAxis === "utc-date"
          ? new Date(Number(x) * 1000).toISOString().slice(0, 10)
          : String(x),
        ...data.series.map((series) =>
          series.values[index] === null ? "" : String(series.values[index])
        ),
      ]),
    }),
    [data, xAxis, xLabel]
  )
  useEffect(() => {
    const target = container.current!
    const readout = tooltip.current
    const css = (name: string) =>
      getComputedStyle(target).getPropertyValue(name).trim()
    const foreground = () => css("--muted-foreground")
    const grid = () => css("--chart-grid")
    const font = getComputedStyle(target).fontFamily
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
          height: 360,
          cursor: {
            y: false,
            drag: { dist: 8 },
            hover: { skip: [] },
            points: { size: 8 },
          },
          hooks: {
            setCursor: [
              (current) => {
                const index = current.cursor.idx
                const visible =
                  index !== null &&
                  index !== undefined &&
                  index >= 0 &&
                  index < data.x.length &&
                  (current.cursor.left ?? -1) >= 0 &&
                  (current.cursor.top ?? -1) >= 0
                selectedIndex.current = visible ? index : null
                if (!tooltip.current) return
                tooltip.current.hidden = !visible
                if (!visible) return
                const value = data.x[index]
                if (xValue.current)
                  xValue.current.textContent =
                    xAxis === "utc-date"
                      ? new Date(Number(value) * 1000)
                          .toISOString()
                          .slice(0, 10)
                      : String(value)
                data.series.forEach((series, seriesIndex) => {
                  const node = yValues.current[seriesIndex]
                  if (node)
                    node.textContent =
                      series.values[index] === null
                        ? "Unavailable"
                        : String(series.values[index])
                })
              },
            ],
          },
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
              stroke: () => css(`--chart-${(index % 5) + 1}`),
              width: 2,
              fill:
                data.type === "bar"
                  ? () => css(`--chart-${(index % 5) + 1}`)
                  : undefined,
              paths: path,
              points: { show: xAxis === "utc-date" },
              spanGaps: false,
            })),
          ],
          axes: [
            {
              stroke: foreground,
              label: xLabel,
              labelSize: 28,
              labelFont: `600 12px ${font}`,
              font: `12px ${font}`,
              grid: { show: true, stroke: grid, width: 0.5 },
              ticks: { show: false },
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
            {
              size: 64,
              stroke: foreground,
              label: yLabel,
              labelSize: 28,
              labelFont: `600 12px ${font}`,
              font: `12px ${font}`,
              values: (_, splits) =>
                splits.map((value) => tickNumber.format(value)),
              grid: { show: true, stroke: grid, width: 0.5 },
              ticks: { show: false },
            },
          ],
        },
        values,
        target
      )
      chart.current = plot
      const current = plot
      resize = new ResizeObserver(() =>
        current.setSize({ width: Math.max(1, target.clientWidth), height: 360 })
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
      chart.current = null
      selectedIndex.current = null
      if (readout) readout.hidden = true
      plot?.destroy()
      target.replaceChildren()
    }
  }, [data, xAxis, xLabel, yLabel])
  useEffect(() => {
    const current = chart.current
    if (!current) return
    current.axes.forEach((axis) => {
      if (axis.grid) axis.grid.show = showGridlines
    })
    current.redraw(false)
  }, [data, xAxis, showGridlines])
  useEffect(() => {
    function dismiss(event: KeyboardEvent) {
      if (event.key === "Escape")
        chart.current?.setCursor({ left: -10, top: -10 })
    }
    document.addEventListener("keydown", dismiss)
    return () => document.removeEventListener("keydown", dismiss)
  }, [])

  async function exportPng() {
    const current = chart.current
    setExportError(null)
    setExporting(true)
    try {
      if (!current) throw new Error("The chart is not ready for PNG export.")
      const blob = await createChartPng(
        current,
        title,
        data.series.map((series) => series.label)
      )
      if (chart.current !== current) return
      const url = URL.createObjectURL(blob)
      try {
        const link = document.createElement("a")
        link.href = url
        link.download =
          (title
            .replace(/[<>:"/\\|?*\p{Cc}]/gu, "_")
            .replace(/[. ]+$/u, "")
            .slice(0, 200) || "chart") + ".png"
        link.click()
      } finally {
        setTimeout(() => URL.revokeObjectURL(url), 0)
      }
    } catch (failure) {
      if (chart.current === current) setExportError(failure)
    } finally {
      setExporting(false)
    }
  }

  return (
    <section
      className="flex min-w-0 flex-col gap-4 border bg-card p-4"
      aria-label={`${title} chart`}
    >
      <div className="flex flex-wrap items-start justify-between gap-3">
        <p className="min-w-0 flex-1 font-semibold break-words">{title}</p>
        <div className="flex flex-wrap gap-2">
          <Button
            variant="outline"
            size="sm"
            aria-pressed={showGridlines}
            onClick={() => setShowGridlines((value) => !value)}
          >
            Gridlines
          </Button>
          <Button
            variant="outline"
            size="sm"
            aria-label={`Export ${title} as PNG`}
            disabled={exporting || error !== null}
            onClick={() => void exportPng()}
          >
            {exporting ? "Exporting..." : "Export PNG"}
          </Button>
        </div>
      </div>
      {error ? <ErrorNotice error={error} /> : null}
      {exportError ? <ErrorNotice error={exportError} /> : null}
      <p className="sr-only">
        X axis: {xLabel}; Y axis: {yLabel}.
      </p>
      <div className="relative min-w-0">
        <div
          ref={container}
          role="img"
          tabIndex={0}
          aria-label={`${title}: ${data.type} chart`}
          aria-describedby={help}
          className="min-w-0 focus-visible:outline-2 focus-visible:outline-ring [&_.u-cursor-x]:border-muted-foreground/40"
          onKeyDown={(event) => {
            const current = chart.current
            if (
              !current ||
              !["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)
            )
              return
            event.preventDefault()
            const previous = selectedIndex.current
            const index =
              event.key === "Home"
                ? 0
                : event.key === "End"
                  ? data.x.length - 1
                  : Math.max(
                      0,
                      Math.min(
                        data.x.length - 1,
                        (previous ??
                          (event.key === "ArrowRight" ? -1 : data.x.length)) +
                          (event.key === "ArrowRight" ? 1 : -1)
                      )
                    )
            const value = data.series.find(
              (series) => series.values[index] !== null
            )?.values[index]
            current.setCursor({
              left: current.valToPos(
                data.type === "bar" ? index : Number(data.x[index]),
                "x"
              ),
              top:
                value === null || value === undefined
                  ? 0
                  : current.valToPos(value, "y"),
            })
          }}
        />
        <div
          ref={tooltip}
          role="tooltip"
          hidden
          className="pointer-events-none absolute top-3 right-3 max-w-[calc(100%-1.5rem)] border bg-popover p-3 text-sm text-popover-foreground shadow-md"
        >
          <p className="font-medium break-words">
            {xLabel}: <span ref={xValue} />
          </p>
          <p className="mt-1 text-xs text-muted-foreground">{yLabel}</p>
          <dl className="mt-2 flex flex-col gap-1">
            {data.series.map((series, index) => (
              <div
                key={index}
                className="flex flex-wrap justify-between gap-x-4 gap-y-1"
              >
                <dt className="break-words">{series.label}</dt>{" "}
                <dd className="font-mono break-all tabular-nums">
                  <span
                    ref={(node) => {
                      yValues.current[index] = node
                    }}
                  />
                </dd>
              </div>
            ))}
          </dl>
        </div>
      </div>
      <p id={help} className="text-xs text-muted-foreground">
        Hover to inspect X/Y values. Focus the chart and use Left/Right or
        Home/End to inspect points; Escape dismisses the readout.
      </p>
      <ul className="flex flex-wrap gap-4" aria-label="Chart series">
        {data.series.map((series, index) => (
          <li key={index} className="break-words">
            <span
              aria-hidden="true"
              className="mr-2 inline-block size-3"
              style={{ backgroundColor: seriesColor(index) }}
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
