import { Blob as NodeBlob } from "node:buffer"
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react"
import type uPlot from "uplot"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { parseChart } from "./chartData"
import ChartReport, { Plot } from "./ChartReport"

const { FakePlot } = vi.hoisted(() => {
  class FakePlot {
    static instances: FakePlot[] = []
    static tzDate = vi.fn((date: Date) => date)
    static paths = {
      bars: vi.fn<(options: uPlot.Series.BarsPathBuilderOpts) => () => void>(
        () => vi.fn()
      ),
    }
    readonly options: uPlot.Options
    readonly data: uPlot.AlignedData
    destroy = vi.fn()
    redraw = vi.fn()
    setSize = vi.fn()
    constructor(
      options: uPlot.Options,
      data: uPlot.AlignedData,
      target: HTMLElement
    ) {
      this.options = options
      this.data = data
      target.appendChild(document.createElement("canvas"))
      FakePlot.instances.push(this)
    }
  }
  return { FakePlot }
})
vi.mock("uplot", () => ({ default: FakePlot }))
const line = {
  type: "line",
  x: [1, 2, 3],
  series: [{ label: "Latency", values: [10, null, 30] }],
}

describe("validated chart data", () => {
  it("preserves aligned finite line values, gaps and category bars", () => {
    expect(parseChart(JSON.stringify(line))).toEqual(line)
    expect(
      parseChart(
        JSON.stringify({
          ...line,
          type: "bar",
          x: ["first", "second", "third"],
        })
      ).x
    ).toEqual(["first", "second", "third"])
  })
  it.each([
    { ...line, x: [2, 1, 3] },
    { ...line, x: [1, 1, 2] },
    { ...line, x: ["1", "2", "3"] },
    { ...line, series: [{ label: "Missing", values: [1] }] },
    { ...line, series: [] },
    { ...line, type: "javascript" },
    { ...line, script: "alert()" },
    { ...line, series: [{ label: "", values: [1, 2, 3] }] },
  ])("rejects inconsistent or unsupported chart input", (value) => {
    expect(() => parseChart(JSON.stringify(value))).toThrow()
  })
  it("rejects invalid JSON and overflowing numeric literals", () => {
    expect(() => parseChart("{")).toThrow("not valid JSON")
    expect(() =>
      parseChart(
        '{"type":"line","x":[1],"series":[{"label":"x","values":[1e400]}]}'
      )
    ).toThrow()
  })
})

describe("owned uPlot chart and tabular alternative", () => {
  beforeEach(() => {
    FakePlot.instances.length = 0
    FakePlot.paths.bars.mockClear()
    vi.stubGlobal("Blob", NodeBlob)
    vi.stubGlobal(
      "ResizeObserver",
      class {
        observe = vi.fn()
        disconnect = vi.fn()
      }
    )
  })
  afterEach(() => {
    vi.unstubAllGlobals()
    document.documentElement.classList.remove("dark")
  })
  it("renders a responsive canvas and exact accessible data, with theme redraw and cleanup", async () => {
    const view = render(
      <ChartReport title="Timing" content={new Blob([JSON.stringify(line)])} />
    )
    await screen.findByRole("img", { name: "Timing: line chart" })
    await waitFor(() => expect(FakePlot.instances).toHaveLength(1))
    const plot = FakePlot.instances[0]!
    expect(plot.data).toEqual([line.x, line.series[0]!.values])
    expect(plot.options.legend).toEqual({ show: false })
    expect(plot.options.axes![1]!.size).toBe(80)
    fireEvent.click(screen.getByText("Chart data table"))
    expect(screen.getByRole("table")).toHaveAttribute("aria-rowcount", "4")
    expect(
      within(screen.getByRole("list", { name: "Chart series" })).getByText(
        "Latency"
      )
    ).toBeVisible()
    act(() => document.documentElement.classList.add("dark"))
    await waitFor(() => expect(plot.redraw).toHaveBeenCalledWith(true))
    view.unmount()
    expect(plot.destroy).toHaveBeenCalledOnce()
  })
  it("uses grouped bar paths rather than hiding overlapping series, without interpreting labels", async () => {
    const label = "<img src=x onerror=alert()>"
    render(
      <ChartReport
        title="Groups"
        content={
          new Blob([
            JSON.stringify({
              type: "bar",
              x: ["one", "two"],
              series: [
                { label, values: [1, 2] },
                { label: "Other", values: [2, 3] },
              ],
            }),
          ])
        }
      />
    )
    await waitFor(() => expect(FakePlot.paths.bars).toHaveBeenCalledOnce())
    expect(
      within(screen.getByRole("list", { name: "Chart series" })).getByText(
        label
      )
    ).toBeVisible()
    expect(document.querySelector("img")).toBeNull()
    const options = FakePlot.paths.bars.mock.calls[0]![0]
    expect(options).toMatchObject({
      disp: {
        x0: { unit: 1 },
        size: { unit: 1 },
      },
    })
  })
  it("surfaces malformed JSON instead of initializing a success-shaped empty chart", async () => {
    render(<ChartReport title="Bad" content={new Blob(["{"])} />)
    expect(await screen.findByRole("alert")).toHaveTextContent("not valid JSON")
    expect(FakePlot.instances).toHaveLength(0)
  })
  it("uses UTC calendar axes and date table cells without changing numeric report axes or null gaps", async () => {
    const x = [Date.UTC(2026, 9, 16) / 1000, Date.UTC(2026, 9, 17) / 1000]
    render(
      <Plot
        title="Daily latency"
        xAxis="utc-date"
        data={{
          type: "line",
          x,
          series: [{ label: "p999", values: [null, 0] }],
        }}
      />
    )
    await waitFor(() => expect(FakePlot.instances).toHaveLength(1))
    const plot = FakePlot.instances[0]!
    expect(plot.options.scales?.x?.time).toBe(true)
    expect(plot.options.series[1]).toMatchObject({
      points: { show: true },
      spanGaps: false,
    })
    expect(plot.data).toEqual([x, [null, 0]])
    const tzDate = plot.options.tzDate!
    tzDate(x[0]!)
    expect(FakePlot.tzDate).toHaveBeenCalledWith(
      new Date("2026-10-16T00:00:00Z"),
      "Etc/UTC"
    )
    const values = plot.options.axes![0]!.values
    if (typeof values !== "function")
      throw new Error("UTC axis formatter missing.")
    expect(values({} as uPlot, x, 0, 86400, 100)).toEqual([
      "2026-10-16",
      "2026-10-17",
    ])
    fireEvent.click(screen.getByText("Chart data table"))
    const table = screen.getByRole("table")
    expect(table).toHaveTextContent("UTC creation day")
    expect(table).toHaveTextContent("2026-10-16")
    expect(table).toHaveTextContent("2026-10-17")
    expect(table).not.toHaveTextContent(String(x[0]))
  })
})
