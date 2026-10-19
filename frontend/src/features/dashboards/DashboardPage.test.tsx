import { useState, type AnchorHTMLAttributes } from "react"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react"
import { beforeEach, describe, expect, it, vi } from "vitest"
import userEvent from "@testing-library/user-event"
import { ApiError } from "@/lib/api/client"
import type { ChartData } from "@/renderers/chart/chartData"
import type { TrendWindow } from "@/features/series/api"
import { DashboardPage } from "./DashboardPage"
import { WorkflowDashboardAccordion } from "./WorkflowDashboardAccordion"
import {
  encodeDashboardId,
  getDashboard,
  type Dashboard,
  type DashboardScope,
} from "./api"

vi.mock("./api", async (original) => ({
  ...(await original<typeof import("./api")>()),
  getDashboard: vi.fn(),
}))
vi.mock("@tanstack/react-router", () => ({
  Link: ({
    children,
    to,
    params,
    search,
    ...props
  }: AnchorHTMLAttributes<HTMLAnchorElement> & {
    to: string
    params?: Record<string, string>
    search?: Record<string, string | number>
  }) => (
    <a
      {...props}
      href={
        Object.entries(params ?? {}).reduce(
          (path, [key, value]) => path.replace(`$${key}`, value),
          to
        ) +
        (search
          ? `?${new URLSearchParams(Object.entries(search).map(([key, value]) => [key, String(value)]))}`
          : "")
      }
    >
      {children}
    </a>
  ),
}))
const { chart } = vi.hoisted(() => ({ chart: vi.fn() }))
vi.mock("@/renderers/chart/ChartReport", () => ({
  Plot: (props: { title: string; data: ChartData; xAxis?: string }) => {
    chart(props)
    return <div role="img" aria-label={props.title} />
  },
}))
const id = encodeDashboardId("team/type/workflow")
const uuid = "019ed132-2f44-7000-8000-000000000001"
const read = vi.mocked(getDashboard)
function fixture(): Dashboard {
  const row = {
    workflowId: "team/type/workflow",
    nodeKey: "alpha",
    observations: 4,
    failures: 1,
    softFailures: 1,
    durationCount: 2,
    failureRate: 0.25,
    p50Ms: 10,
    p95Ms: 20,
    cpuMaxPercent: 150,
    ramPeakMb: 512,
    latestRunId: uuid,
    runHref: `/runs/${uuid}`,
  }
  return {
    scope: "workflow",
    id,
    window: "7d",
    from: "2026-10-11",
    to: "2026-10-18",
    top: 10,
    observationKind: "Node",
    rankings: { slowest: [row], failureRate: [row], cpu: [row], ram: [row] },
    trends: {
      total: {
        runs: 4,
        completed: 4,
        durationCount: 2,
        status: {
          queued: 0,
          running: 0,
          aborting: 0,
          succeeded: 3,
          failed: 1,
          aborted: 0,
          timedOut: 0,
        },
        failureRate: 0.25,
        p50Ms: 10,
        p90Ms: 18,
        p99Ms: 19.8,
        p999Ms: 19.98,
        cpuMaxPercent: 150,
        ramPeakMb: 512,
      },
      days: Array.from({ length: 7 }, (_, i) => ({
        day: `2026-10-${11 + i}`,
        metrics: {
          runs: i === 6 ? 4 : 0,
          completed: i === 6 ? 4 : 0,
          durationCount: i === 6 ? 2 : 0,
          status: {
            queued: 0,
            running: 0,
            aborting: 0,
            succeeded: i === 6 ? 3 : 0,
            failed: i === 6 ? 1 : 0,
            aborted: 0,
            timedOut: 0,
          },
          failureRate: i === 6 ? 0.25 : null,
          p50Ms: i === 6 ? 10 : null,
          p90Ms: i === 6 ? 18 : null,
          p99Ms: i === 6 ? 19.8 : null,
          p999Ms: i === 6 ? 19.98 : null,
          cpuMaxPercent: i === 6 ? 150 : null,
          ramPeakMb: i === 6 ? 512 : null,
        },
      })),
    },
  }
}
function show(scope: DashboardScope = "workflow", resourceId = id) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  function Content() {
    const [search, setSearch] = useState<{ window: TrendWindow; top: number }>({
      window: "7d",
      top: 10,
    })
    return (
      <DashboardPage
        scope={scope}
        id={resourceId}
        {...search}
        onChange={setSearch}
      />
    )
  }
  render(
    <QueryClientProvider client={client}>
      <Content />
    </QueryClientProvider>
  )
  return client
}
function showAccordion(workflowId = "team/type/workflow") {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  const tree = (workflow: string) => (
    <QueryClientProvider client={client}>
      <h1>Workflow history</h1>
      <WorkflowDashboardAccordion key={workflow} workflowId={workflow} />
    </QueryClientProvider>
  )
  const rendered = render(tree(workflowId))
  return {
    client,
    rerender: (workflow: string) => rendered.rerender(tree(workflow)),
  }
}
describe("accessible metric dashboard", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    read.mockResolvedValue(fixture())
  })
  it("keeps history metrics collapsed without reads, then embeds the existing dashboard with nested headings", async () => {
    showAccordion()
    const toggle = screen.getByText("Metrics dashboard", { exact: true })
    expect(toggle.closest("details")).not.toHaveAttribute("open")
    expect(
      screen.queryByRole("region", { name: "Metric dashboard" })
    ).not.toBeInTheDocument()
    expect(read).not.toHaveBeenCalled()
    await userEvent.click(toggle)
    await screen.findByRole("img", { name: "Workflow execution latency (ms)" })
    expect(read.mock.calls[0]?.slice(0, 4)).toEqual(["workflow", id, "30d", 10])
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent(
      "Workflow history"
    )
    expect(
      screen.getByRole("heading", { level: 2, name: "workflow dashboard" })
    ).toBeVisible()
    expect(
      screen.getByRole("heading", { level: 3, name: "Workflow trends" })
    ).toBeVisible()
    expect(
      screen.getByRole("heading", {
        level: 4,
        name: "Workflow execution latency (ms)",
      })
    ).toBeVisible()
    expect(
      screen.getByRole("heading", { level: 3, name: "Slowest nodes" })
    ).toBeVisible()
  })
  it("preserves metric controls across collapse and rechecks access instead of exposing cached charts on reopen", async () => {
    showAccordion()
    const toggle = screen.getByText("Metrics dashboard", { exact: true })
    await userEvent.click(toggle)
    await screen.findByRole("img", { name: "Workflow execution latency (ms)" })
    fireEvent.change(screen.getByLabelText("Dashboard window"), {
      target: { value: "90d" },
    })
    await waitFor(() =>
      expect(read.mock.calls.at(-1)?.slice(2, 4)).toEqual(["90d", 10])
    )
    await screen.findByRole("img", { name: "Workflow execution latency (ms)" })
    fireEvent.change(screen.getByLabelText("Top rankings"), {
      target: { value: "25" },
    })
    await waitFor(() =>
      expect(read.mock.calls.at(-1)?.slice(2, 4)).toEqual(["90d", 25])
    )
    await screen.findByRole("img", { name: "Workflow execution latency (ms)" })
    await userEvent.click(toggle)
    await waitFor(() =>
      expect(screen.queryByRole("img")).not.toBeInTheDocument()
    )
    const reads = read.mock.calls.length
    read.mockRejectedValueOnce(
      new ApiError(new Response(null, { status: 403 }), {
        title: "Viewer permission denied.",
      })
    )
    await userEvent.click(toggle)
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Viewer permission denied."
    )
    expect(read).toHaveBeenCalledTimes(reads + 1)
    expect(screen.getByLabelText("Dashboard window")).toHaveValue("90d")
    expect(screen.getByLabelText("Top rankings")).toHaveValue("25")
    expect(screen.queryByRole("img")).not.toBeInTheDocument()
    expect(screen.queryByRole("table")).not.toBeInTheDocument()
  })
  it("aborts an in-flight dashboard read when collapsed", async () => {
    read.mockImplementationOnce(() => new Promise(() => {}))
    showAccordion()
    const toggle = screen.getByText("Metrics dashboard", { exact: true })
    await userEvent.click(toggle)
    await waitFor(() => expect(read).toHaveBeenCalledOnce())
    const signal = read.mock.calls[0]![4]!
    expect(signal.aborted).toBe(false)
    await userEvent.click(toggle)
    await waitFor(() => expect(signal.aborted).toBe(true))
    expect(
      screen.queryByRole("region", { name: "Metric dashboard" })
    ).not.toBeInTheDocument()
  })
  it("resets the accordion and controls when navigating to another workflow", async () => {
    const shown = showAccordion()
    await userEvent.click(
      screen.getByText("Metrics dashboard", { exact: true })
    )
    await screen.findByRole("img", { name: "Workflow execution latency (ms)" })
    const reads = read.mock.calls.length
    shown.rerender("team/type/another-workflow")
    expect(
      screen.getByText("Metrics dashboard", { exact: true }).closest("details")
    ).not.toHaveAttribute("open")
    expect(
      screen.queryByRole("region", { name: "Metric dashboard" })
    ).not.toBeInTheDocument()
    expect(read).toHaveBeenCalledTimes(reads)
    await userEvent.click(
      screen.getByText("Metrics dashboard", { exact: true })
    )
    await waitFor(() =>
      expect(read.mock.calls.at(-1)?.slice(0, 4)).toEqual([
        "workflow",
        encodeDashboardId("team/type/another-workflow"),
        "30d",
        10,
      ])
    )
  })
  it("renders four true daily lines, one status bar and linked ranking tables", async () => {
    show()
    const table = await screen.findByRole("table", {
      name: "Slowest nodes: values and run drill-down",
    })
    expect(screen.getAllByRole("img")).toHaveLength(5)
    expect(chart.mock.calls.map(([props]) => props.data.type)).toEqual([
      "line",
      "line",
      "line",
      "line",
      "bar",
    ])
    const latency = chart.mock.calls[0]![0]
    expect(latency.xAxis).toBe("utc-date")
    expect(latency.data.x).toEqual(
      Array.from({ length: 7 }, (_, i) => Date.UTC(2026, 9, 11 + i) / 1000)
    )
    expect(latency.data.series).toEqual([
      { label: "p50", values: [null, null, null, null, null, null, 10] },
      { label: "p90", values: [null, null, null, null, null, null, 18] },
      { label: "p99", values: [null, null, null, null, null, null, 19.8] },
      {
        label: "p99.9 (p999)",
        values: [null, null, null, null, null, null, 19.98],
      },
    ])
    expect(chart.mock.calls[1]![0].data.series[0].values).toEqual([
      null,
      null,
      null,
      null,
      null,
      null,
      25,
    ])
    expect(chart.mock.calls[4]![0].data).toEqual({
      type: "bar",
      x: [
        "Queued",
        "Running",
        "Aborting",
        "Succeeded",
        "Failed",
        "Aborted",
        "Timed out",
      ],
      series: [{ label: "Runs", values: [0, 0, 0, 3, 1, 0, 0] }],
    })
    expect(screen.getAllByRole("table")).toHaveLength(4)
    expect(within(table).getAllByRole("row")).toHaveLength(2)
    expect(table).toHaveTextContent("20 ms")
    expect(
      screen.getByRole("table", {
        name: "Highest failure rate: values and run drill-down",
      })
    ).toHaveTextContent("25%")
    expect(
      screen.getByRole("table", {
        name: "Highest CPU: values and run drill-down",
      })
    ).toHaveTextContent("150 %")
    const link = within(table).getByRole("link", { name: "alpha" })
    const search = new URL(link.getAttribute("href")!, "http://localhost")
      .searchParams
    expect(search.get("workflowId")).toBe("team/type/workflow")
    expect(search.get("from")).toBe("2026-10-11T00:00:00Z")
    expect(search.get("to")).toBe("2026-10-18T00:00:00Z")
    expect(
      within(table).getByRole("link", { name: "Latest contribution" })
    ).toHaveAttribute("href", `/runs/${uuid}`)
    expect(
      within(table).getByRole("link", { name: "Node dashboard" })
    ).toHaveAttribute(
      "href",
      expect.stringContaining(encodeDashboardId("team/type/workflow/alpha"))
    )
  })
  it("announces empty windows without inventing charts or values", async () => {
    const data = fixture()
    data.rankings = { slowest: [], failureRate: [], cpu: [], ram: [] }
    const empty = data.trends!.days[0]!.metrics
    data.trends = {
      total: empty,
      days: data.trends!.days.map(({ day }) => ({ day, metrics: empty })),
    }
    read.mockResolvedValue(data)
    show()
    expect(
      await screen.findByText("No completed observations in this window.")
    ).toBeInTheDocument()
    expect(screen.queryByRole("img")).not.toBeInTheDocument()
    expect(screen.queryByRole("table")).not.toBeInTheDocument()
  })
  it("changes window and top without displaying another cached selection", async () => {
    show()
    await screen.findByRole("table", {
      name: "Slowest nodes: values and run drill-down",
    })
    read.mockImplementation(() => new Promise(() => {}))
    fireEvent.change(screen.getByLabelText("Dashboard window"), {
      target: { value: "90d" },
    })
    await waitFor(() =>
      expect(read.mock.calls.at(-1)?.slice(2, 4)).toEqual(["90d", 10])
    )
    expect(screen.queryByRole("table")).not.toBeInTheDocument()
    fireEvent.change(screen.getByLabelText("Top rankings"), {
      target: { value: "25" },
    })
    await waitFor(() =>
      expect(read.mock.calls.at(-1)?.slice(2, 4)).toEqual(["90d", 25])
    )
  })
  it("hides stale tables while access revalidates and after real denial, without a retry loop", async () => {
    const client = show()
    await screen.findByRole("table", {
      name: "Slowest nodes: values and run drill-down",
    })
    let reject: (error: Error) => void = () => {
      throw new Error("Read was not pending.")
    }
    read.mockImplementationOnce(
      () =>
        new Promise((_, fail) => {
          reject = fail
        })
    )
    fireEvent.click(screen.getByRole("button", { name: "Refresh dashboard" }))
    await waitFor(() =>
      expect(screen.queryByRole("table")).not.toBeInTheDocument()
    )
    reject(
      new ApiError(new Response(null, { status: 403 }), {
        title: "Viewer permission denied.",
      })
    )
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Viewer permission denied."
    )
    expect(screen.queryByRole("img")).not.toBeInTheDocument()
    expect(read).toHaveBeenCalledTimes(2)
    expect(
      client.getQueryData(["dashboards", "workflow", id, "7d", 10])
    ).toBeDefined()
    read.mockResolvedValue(fixture())
    fireEvent.click(screen.getByRole("button", { name: "Refresh dashboard" }))
    await screen.findByRole("table", {
      name: "Slowest nodes: values and run drill-down",
    })
  })
  it("shows explicit storage failure and permits a successful retry", async () => {
    read.mockRejectedValueOnce(new Error("Stored dashboards unavailable."))
    show()
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Stored dashboards unavailable."
    )
    expect(screen.queryByRole("table")).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole("button", { name: "Refresh dashboard" }))
    await screen.findByRole("table", {
      name: "Slowest nodes: values and run drill-down",
    })
  })
  it("allows scoped machine analytics without demanding broader inventory permission", async () => {
    read.mockResolvedValue({ ...fixture(), trends: null })
    show("machine", uuid)
    await screen.findByRole("table", {
      name: "Slowest nodes: values and run drill-down",
    })
    expect(read.mock.calls[0]?.slice(0, 2)).toEqual(["machine", uuid])
    expect(screen.getByText(/exact machine, restricted/)).toBeInTheDocument()
    expect(chart.mock.calls.map(([props]) => props.data.type)).toEqual([
      "bar",
      "bar",
      "bar",
      "bar",
    ])
  })
  it("keeps measured zero as a line value without manufacturing missing latency or CPU", async () => {
    const data = fixture()
    for (const metrics of [data.trends!.total, data.trends!.days[6]!.metrics]) {
      Object.assign(metrics, {
        durationCount: 0,
        p50Ms: null,
        p90Ms: null,
        p99Ms: null,
        p999Ms: null,
        cpuMaxPercent: null,
        ramPeakMb: 0,
      })
    }
    read.mockResolvedValue(data)
    show()
    await screen.findByRole("img", { name: "Workflow status counts" })
    expect(
      screen.queryByRole("img", { name: "Workflow execution latency (ms)" })
    ).not.toBeInTheDocument()
    expect(
      screen.queryByRole("img", { name: "Daily maximum node CPU (%)" })
    ).not.toBeInTheDocument()
    expect(
      chart.mock.calls
        .find(([props]) => props.title === "Daily maximum node RAM (MiB)")![0]
        .data.series[0].values.at(-1)
    ).toBe(0)
  })
})
