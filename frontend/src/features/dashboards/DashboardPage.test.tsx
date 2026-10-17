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
import { ApiError } from "@/lib/api/client"
import type { TrendWindow } from "@/features/series/api"
import { DashboardPage } from "./DashboardPage"
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
vi.mock("@/renderers/chart/ChartReport", () => ({
  Plot: ({ title }: { title: string }) => <div role="img" aria-label={title} />,
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
describe("accessible metric dashboard", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    read.mockResolvedValue(fixture())
  })
  it("renders four exact ranked charts and linked table alternatives with honest scope semantics", async () => {
    show()
    const table = await screen.findByRole("table", {
      name: "Slowest nodes: values and run drill-down",
    })
    expect(screen.getAllByRole("img")).toHaveLength(4)
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
    show("machine", uuid)
    await screen.findByRole("table", {
      name: "Slowest nodes: values and run drill-down",
    })
    expect(read.mock.calls[0]?.slice(0, 2)).toEqual(["machine", uuid])
    expect(screen.getByText(/exact machine, restricted/)).toBeInTheDocument()
  })
})
