import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import type { ChartData } from "@/renderers/chart/chartData"
import {
  getResourcePage,
  getResourceSummary,
  type ResourcePage,
  type ResourcePoint,
  type ResourceSummary,
} from "./resource-api"
import { ResourceCharts, resourcePlots } from "./ResourceCharts"

vi.mock("./resource-api", async (original) => ({
  ...(await original<typeof import("./resource-api")>()),
  getResourcePage: vi.fn(),
  getResourceSummary: vi.fn(),
}))
vi.mock("@/renderers/chart/ChartReport", () => ({
  Plot: ({ title, data }: { title: string; data: ChartData }) => (
    <div role="img" aria-label={title}>
      {JSON.stringify(data)}
    </div>
  ),
}))
const runId = "019ed132-2f44-7000-8000-000000000001"
const summary: ResourceSummary = {
  taskKey: "work/task",
  attempt: 1,
  agentId: null,
  sampleCount: "61",
  averageCpuPercent: 125,
  maxCpuPercent: 150,
  peakRamBytes: "18446744073709551615",
  networkSentBytes: "9007199254740993",
  networkReceivedBytes: "0",
  cpuObservedSeconds: 60,
  networkObservedSeconds: 60,
  cpuUnavailableReason: null,
  memoryUnavailableReason: null,
  networkUnavailableReason: null,
}
const point: ResourcePoint = {
  atUtc: "2026-10-16T12:00:00Z",
  intervalSeconds: 1,
  cpuPercent: 0,
  ramBytes: "1024",
  networkSentBytes: "0",
  networkReceivedBytes: "0",
  networkSentDeltaBytes: "0",
  networkReceivedDeltaBytes: "0",
  networkSentBytesPerSecond: 0,
  networkReceivedBytesPerSecond: 0,
  cpuScope: "job-with-descendants",
  memoryScope: "job-resident-sum",
  networkScope: "host-process-etw-transport",
  cpuUnavailableReason: null,
  memoryUnavailableReason: null,
  networkUnavailableReason: null,
  networkRateUnavailableReason: null,
}
function page(count = 60, start = 61): ResourcePage {
  return {
    samples: Array.from({ length: count }, (_, index) => ({
      sequence: String(start - index),
      observation: {
        task: point,
        machine: null,
        machineUnavailableReason: "Machine observation not available.",
      },
    })),
    nextCursor: count === 60 ? "eA.eQ" : null,
  }
}
function show() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  return {
    ...render(
      <QueryClientProvider client={client}>
        <ResourceCharts runId={runId} />
      </QueryClientProvider>
    ),
    client,
  }
}
describe("resource charts", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    vi.mocked(getResourceSummary).mockResolvedValue([summary])
    vi.mocked(getResourcePage).mockResolvedValue(page())
  })
  it("renders all metrics and exact byte summaries, but never claims one-core task CPU is capped at 100%", async () => {
    show()
    const table = await screen.findByRole("region", {
      name: "Attempt resource summaries",
    })
    expect(table).toHaveTextContent("125%")
    expect(table).toHaveTextContent("18,446,744,073,709,551,615 B")
    expect(table).toHaveTextContent("9,007,199,254,740,993 B")
    expect(table).toHaveTextContent("0 B")
    expect(
      await screen.findByRole("img", { name: "Task CPU (%)" })
    ).toBeVisible()
    expect(screen.getAllByRole("img")).toHaveLength(3)
    expect(screen.getByText(/multithreaded work can exceed 100%/)).toBeVisible()
  })
  it("keeps each page bounded, navigates by the opaque cursor and resets to the newest page on refresh", async () => {
    vi.mocked(getResourcePage).mockImplementation(
      async (_run, _selected, cursor) => (cursor ? page(1, 1) : page())
    )
    show()
    let exact = await screen.findByRole("region", {
      name: "Exact resource samples",
    })
    expect(within(exact).getAllByRole("row")).toHaveLength(61)
    expect(screen.getByRole("button", { name: "Newer samples" })).toBeDisabled()
    fireEvent.click(screen.getByRole("button", { name: "Older samples" }))
    await waitFor(() =>
      expect(getResourcePage).toHaveBeenLastCalledWith(
        runId,
        summary,
        { token: "eA.eQ", before: "2" },
        expect.any(AbortSignal)
      )
    )
    exact = await screen.findByRole("region", {
      name: "Exact resource samples",
    })
    await waitFor(() =>
      expect(within(exact).getAllByRole("row")).toHaveLength(2)
    )
    expect(screen.getByRole("button", { name: "Older samples" })).toBeDisabled()
    fireEvent.click(
      screen.getByRole("button", { name: "Refresh resource metrics" })
    )
    await waitFor(() =>
      expect(getResourcePage).toHaveBeenLastCalledWith(
        runId,
        summary,
        null,
        expect.any(AbortSignal)
      )
    )
    expect(
      within(
        await screen.findByRole("region", { name: "Exact resource samples" })
      ).getAllByRole("row")
    ).toHaveLength(61)
  })
  it("exposes honest missing machine context without drawing fabricated zero series", async () => {
    show()
    await screen.findByRole("region", { name: "Exact resource samples" })
    await userEvent
      .setup()
      .selectOptions(screen.getByLabelText("Resource scope"), "machine")
    expect(
      screen.getByText(/Machine observations are unavailable for this page/)
    ).toBeVisible()
    expect(screen.queryByRole("img")).not.toBeInTheDocument()
    expect(
      within(
        screen.getByRole("region", { name: "Exact resource samples" })
      ).getAllByText("Unavailable: Machine observation not available.")
    ).toHaveLength(60)
  })
  it("preserves null baseline gaps separately from genuine zero and orders plots by durable sequence, not UTC", () => {
    const data = page(2, 2)
    const first = data.samples[1]!.observation
    data.samples[1]!.observation = {
      ...first,
      task: {
        ...point,
        atUtc: "2026-10-16T13:00:00Z",
        cpuPercent: null,
        cpuUnavailableReason: "First CPU baseline.",
        networkSentBytesPerSecond: null,
        networkReceivedBytesPerSecond: null,
        networkRateUnavailableReason: "First network baseline.",
      },
    }
    const plots = resourcePlots(data, "task")
    expect(plots.map((plot) => plot.data.xLabel)).toEqual(
      Array(3).fill("Sample order (page)")
    )
    expect(plots.map((plot) => plot.data.yLabel)).toEqual([
      "CPU (%)",
      "RAM (MiB)",
      "Network rate (B/s)",
    ])
    expect(plots[0]!.data.x).toEqual([1, 2])
    expect(plots[0]!.data.series[0]!.values).toEqual([null, 0])
    expect(plots[2]!.data.series[0]!.values).toEqual([null, 0])
  })
  it.each(["summary", "series"])(
    "hides all cached metrics after an authoritative %s denial and permits explicit retry",
    async (where) => {
      const { client } = show()
      await screen.findByRole("region", { name: "Exact resource samples" })
      const failure = new ApiError(new Response(null, { status: 403 }), {
        title: "Forbidden.",
      })
      if (where === "summary")
        vi.mocked(getResourceSummary).mockRejectedValue(failure)
      else vi.mocked(getResourcePage).mockRejectedValue(failure)
      await client.invalidateQueries({
        queryKey: [
          where === "summary" ? "resource-summary" : "resource-series",
          runId,
        ],
      })
      expect(await screen.findByRole("alert")).toHaveTextContent("Forbidden.")
      expect(screen.queryByRole("table")).not.toBeInTheDocument()
      expect(screen.queryByRole("img")).not.toBeInTheDocument()
      vi.mocked(getResourceSummary).mockResolvedValue([summary])
      vi.mocked(getResourcePage).mockResolvedValue(page())
      fireEvent.click(
        screen.getByRole("button", { name: "Refresh resource metrics" })
      )
      expect(
        await screen.findByRole("region", { name: "Exact resource samples" })
      ).toBeVisible()
    }
  )
  it("shows empty attempts and storage failures explicitly, without retaining partial chart data", async () => {
    vi.mocked(getResourceSummary).mockResolvedValueOnce([])
    show()
    expect(
      await screen.findByText(
        "No dispatched attempts have resource metrics yet."
      )
    ).toBeVisible()
    fireEvent.click(
      screen.getByRole("button", { name: "Refresh resource metrics" })
    )
    await screen.findByRole("region", { name: "Exact resource samples" })
    vi.mocked(getResourcePage).mockRejectedValue(
      new Error("Resource storage unavailable.")
    )
    fireEvent.click(
      screen.getByRole("button", { name: "Refresh resource metrics" })
    )
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Resource storage unavailable."
    )
    expect(
      screen.queryByRole("region", { name: "Exact resource samples" })
    ).not.toBeInTheDocument()
    expect(screen.queryByRole("img")).not.toBeInTheDocument()
  })
  it("aborts stale attempt reads and never paints their late response over the selected attempt", async () => {
    const second = { ...summary, taskKey: "work/second", sampleCount: "1" }
    vi.mocked(getResourceSummary).mockResolvedValue([summary, second])
    let resolve: ((value: ResourcePage) => void) | undefined
    const slow = new Promise<ResourcePage>((done) => {
      resolve = done
    })
    let aborted = false
    const current = page(1, 1)
    current.samples[0]!.observation.task = { ...point, cpuPercent: 0.000001 }
    vi.mocked(getResourcePage).mockImplementation(
      (_run, selected, _cursor, signal) => {
        if (selected.taskKey === second.taskKey) return Promise.resolve(current)
        signal?.addEventListener("abort", () => {
          aborted = true
        })
        return slow
      }
    )
    show()
    const select = await screen.findByLabelText("Resource attempt")
    await waitFor(() => expect(getResourcePage).toHaveBeenCalledTimes(1))
    await userEvent
      .setup()
      .selectOptions(select, JSON.stringify([second.taskKey, 1]))
    const exact = await screen.findByRole("region", {
      name: "Exact resource samples",
    })
    expect(exact).toHaveTextContent("0.000001%")
    expect(aborted).toBe(true)
    await act(async () => {
      resolve?.(page())
    })
    expect(within(exact).getAllByRole("row")).toHaveLength(2)
    expect(exact).toHaveTextContent("0.000001%")
  })
})
