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
import ChartReport from "./ChartReport"

const { FakePlot } = vi.hoisted(() => {
  class FakePlot {
    static instances: FakePlot[] = []
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
})
