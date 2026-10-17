import { useState } from "react"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import { getTrends, type SeriesTrends, type TrendWindow } from "./api"
import { TrendsPanel } from "./TrendsPanel"

const state = vi.hoisted(() => ({
  permitted: true,
  checking: false,
  permissionError: null as Error | null,
}))
vi.mock("@/lib/auth/usePermissions", () => ({
  usePermissions: (resource: { scope: string; id: string }) => ({
    can: () => state.permitted && !state.checking && !state.permissionError,
    isPending: false,
    isFetching: state.checking,
    isSuccess: !state.permissionError,
    data: { ...resource, viewer: state.permitted },
    error: state.permissionError,
  }),
}))
vi.mock("./api", async (original) => ({
  ...(await original<typeof import("./api")>()),
  getTrends: vi.fn(),
}))
vi.mock("@/renderers/chart/ChartReport", () => ({
  Plot: ({ title, data }: { title: string; data: { x: string[] } }) => (
    <div
      role="img"
      aria-label={`${title}: bar chart`}
      data-points={data.x.length}
    />
  ),
}))
const id = "019ed132-2f44-7000-8000-000000000001"
const read = vi.mocked(getTrends)
function fixture(window: TrendWindow = "7d"): SeriesTrends {
  const days = Number(window.slice(0, -1))
  const from = Date.parse("2026-10-18") - days * 86400000
  const metrics = {
    runs: 2,
    succeeded: 1,
    failed: 1,
    durationCount: 1,
    successRate: 0.5,
    p50Ms: 12,
    p95Ms: 12,
  }
  return {
    seriesId: id,
    window,
    from: new Date(from).toISOString().slice(0, 10),
    to: "2026-10-18",
    totals: {
      ...metrics,
      runs: days * 2,
      succeeded: days,
      failed: days,
      durationCount: days,
    },
    streak: { outcome: "Failed", runs: 1 },
    days: Array.from({ length: days }, (_, index) => ({
      day: new Date(from + index * 86400000).toISOString().slice(0, 10),
      metrics: { ...metrics },
    })),
  }
}
function show(window: TrendWindow = "7d") {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  function Content() {
    const [period, setPeriod] = useState(window)
    return (
      <TrendsPanel seriesId={id} window={period} onWindowChange={setPeriod} />
    )
  }
  const tree = () => (
    <QueryClientProvider client={client}>
      <Content />
    </QueryClientProvider>
  )
  const result = render(tree())
  return { client, rerender: () => result.rerender(tree()) }
}
describe("accessible series trends panel", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    state.permitted = true
    state.checking = false
    state.permissionError = null
  })
  it("shows exact counts, rates, measured duration, failure streak, UTC charts and table", async () => {
    read.mockResolvedValue(fixture())
    show()
    expect(screen.getByRole("status")).toHaveTextContent("Loading trends")
    const table = await screen.findByRole("table")
    expect(table).toHaveAccessibleName(
      "Daily series trends (UTC), tabular chart alternative"
    )
    expect(within(table).getAllByRole("row")).toHaveLength(8)
    expect(
      within(table).getByRole("row", { name: /2026-10-11/ })
    ).toHaveTextContent("50%")
    expect(
      screen.getByText("Current streak").nextElementSibling
    ).toHaveTextContent("1 failed")
    expect(
      screen.getByText("Measured durations", { selector: "dt" })
        .nextElementSibling
    ).toHaveTextContent("7")
    expect(
      screen.getByRole("img", { name: "Daily duration (ms): bar chart" })
    ).toHaveAttribute("data-points", "7")
    expect(
      screen.getByRole("img", { name: "Daily success rate (%): bar chart" })
    ).toBeInTheDocument()
  })
  it("announces genuine zero-run windows with null measurements and no fabricated charts", async () => {
    const data = fixture()
    const metrics = {
      runs: 0,
      succeeded: 0,
      failed: 0,
      durationCount: 0,
      successRate: null,
      p50Ms: null,
      p95Ms: null,
    }
    data.totals = metrics
    data.days = data.days.map((day) => ({ ...day, metrics }))
    data.streak = { outcome: null, runs: 0 }
    read.mockResolvedValue(data)
    show()
    expect(
      await screen.findByText("No completed runs in this window.")
    ).toBeInTheDocument()
    expect(screen.getAllByText("Not recorded").length).toBeGreaterThan(3)
    expect(screen.queryByRole("img")).not.toBeInTheDocument()
    expect(within(screen.getByRole("table")).getAllByRole("row")).toHaveLength(
      8
    )
  })
  it("fetches the selected window without temporarily presenting another period's data", async () => {
    read.mockResolvedValueOnce(fixture())
    show()
    await screen.findByRole("table")
    read.mockImplementation(() => new Promise(() => {}))
    fireEvent.change(screen.getByLabelText("Trend window"), {
      target: { value: "90d" },
    })
    await waitFor(() => expect(read.mock.calls.at(-1)?.[1]).toBe("90d"))
    expect(screen.getByText("Loading trends...")).toBeInTheDocument()
    expect(screen.queryByRole("table")).not.toBeInTheDocument()
  })
  it("surfaces failed refresh and hides cached charts/table until an explicit successful retry", async () => {
    read.mockResolvedValueOnce(fixture())
    show()
    await screen.findByRole("table")
    read.mockRejectedValueOnce(new Error("Stored trends unavailable."))
    fireEvent.click(screen.getByRole("button", { name: "Refresh trends" }))
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Stored trends unavailable."
    )
    expect(screen.queryByRole("table")).not.toBeInTheDocument()
    expect(screen.queryByRole("img")).not.toBeInTheDocument()
    read.mockResolvedValue(fixture())
    fireEvent.click(screen.getByRole("button", { name: "Refresh trends" }))
    await screen.findByRole("table")
    expect(read).toHaveBeenCalledTimes(3)
  })
  it("denies permission before any request and hides cached data during current permission revalidation or revocation", async () => {
    state.permitted = false
    read.mockResolvedValue(fixture())
    const view = show()
    expect(screen.getByRole("alert")).toHaveTextContent(
      "Series Viewer permission"
    )
    expect(read).not.toHaveBeenCalled()
    state.permitted = true
    view.rerender()
    await screen.findByRole("table")
    state.checking = true
    view.rerender()
    expect(
      screen.getByText("Checking series permissions...")
    ).toBeInTheDocument()
    expect(screen.queryByRole("table")).not.toBeInTheDocument()
    state.checking = false
    state.permitted = false
    view.rerender()
    expect(screen.getByRole("alert")).toHaveTextContent(
      "Series Viewer permission"
    )
    expect(screen.queryByRole("img")).not.toBeInTheDocument()
  })
  it("invalidates current permissions after authoritative 403 and never presents a cached success", async () => {
    read.mockRejectedValue(
      new ApiError(new Response(null, { status: 403 }), {
        title: "Scope denied.",
      })
    )
    const view = show()
    const invalidate = vi.spyOn(view.client, "invalidateQueries")
    expect(await screen.findByRole("alert")).toHaveTextContent("Scope denied")
    expect(invalidate).toHaveBeenCalledWith({
      queryKey: ["permissions", "series", id],
    })
    expect(read).toHaveBeenCalledTimes(1)
    expect(screen.queryByRole("table")).not.toBeInTheDocument()
    state.checking = true
    view.rerender()
    expect(
      screen.getByText("Checking series permissions...")
    ).toBeInTheDocument()
    state.checking = false
    view.rerender()
    await waitFor(() =>
      expect(view.client.isFetching({ queryKey: ["series-trends"] })).toBe(0)
    )
    expect(read).toHaveBeenCalledTimes(1)
  })
  it("renders all 90 daily rows and chart points under 500ms", async () => {
    read.mockResolvedValue(fixture("90d"))
    const start = performance.now()
    show("90d")
    const table = await screen.findByRole("table")
    expect(within(table).getAllByRole("row")).toHaveLength(91)
    expect(
      screen.getByRole("img", { name: "Daily duration (ms): bar chart" })
    ).toHaveAttribute("data-points", "90")
    expect(performance.now() - start).toBeLessThan(500)
  })
})
