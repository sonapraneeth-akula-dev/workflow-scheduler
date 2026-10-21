import type { AnchorHTMLAttributes } from "react"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { act, render, screen, waitFor, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { SystemView } from "./SystemView"
import { getSystem, systemFrame, type SystemObservation } from "./api"
import { observation } from "./testFixture"
import { ApiError } from "@/lib/api/client"

const state = vi.hoisted(() => ({
  permitted: true,
  checking: false,
  error: null as Error | null,
}))
vi.mock("@/lib/auth/usePermissions", () => ({
  usePermissions: () => ({
    can: () => state.permitted && !state.checking,
    isSuccess: !state.error,
    data: { admin: state.permitted },
    error: state.error,
  }),
}))
vi.mock("@tanstack/react-router", () => ({
  Link: ({
    children,
    to,
    params,
    ...props
  }: AnchorHTMLAttributes<HTMLAnchorElement> & {
    to: string
    params?: { id: string }
  }) => (
    <a {...props} href={to.replace("$id", params?.id ?? "")}>
      {children}
    </a>
  ),
}))
vi.mock("./api", async (original) => ({
  ...(await original<typeof import("./api")>()),
  getSystem: vi.fn(),
}))

function show() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  const tree = () => (
    <QueryClientProvider client={client}>
      <SystemView />
    </QueryClientProvider>
  )
  const rendered = render(tree())
  return { client, ...rendered, rerender: () => rendered.rerender(tree()) }
}

describe("live admin system view", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    state.permitted = true
    state.checking = false
    state.error = null
    vi.mocked(getSystem).mockResolvedValue(structuredClone(observation))
  })
  it("renders real roles, placements, worker counts and a keyboard-accessible diagram without executing names", async () => {
    show()
    const instances = await screen.findByRole("region", {
      name: "Application instance inventory",
    })
    expect(within(instances).getByText("kardia-api <script>")).toBeVisible()
    expect(within(instances).getByText("Node: kardia-lab-1")).toBeVisible()
    expect(within(instances).getByText("Active leader")).toBeVisible()
    expect(within(instances).getByText("100 / 20")).toBeVisible()
    const workers = screen.getByRole("region", {
      name: "Worker assignment inventory",
    })
    expect(
      within(workers).getByRole("link", { name: "kardia-worker-0" })
    ).toHaveAttribute("href", `/machines/${observation.snapshot.workers[0].id}`)
    expect(within(workers).getByText("2 / 3")).toBeVisible()
    expect(
      screen.getByRole("region", { name: "Observed application flow" })
    ).toHaveAttribute("tabindex", "0")
    expect(screen.getByText(/not packet traces/)).toBeVisible()
    expect(document.querySelector("script")).toBeNull()
  })
  it("hides cached inventory during permission revalidation, denial and authorization failure", async () => {
    const shown = show()
    await screen.findByRole("table", { name: "API and controller processes" })
    state.checking = true
    shown.rerender()
    expect(screen.queryByRole("table")).toBeNull()
    state.checking = false
    state.permitted = false
    shown.rerender()
    expect(screen.getByRole("alert")).toHaveTextContent("Global Admin")
    expect(screen.queryByText("kardia-worker-0")).toBeNull()
    state.error = new Error("Permission service unavailable")
    shown.rerender()
    expect(screen.getByRole("alert")).toHaveTextContent(
      "Permission service unavailable"
    )
    expect(screen.queryByRole("table")).toBeNull()
  })
  it("never fetches global inventory for an unpermitted caller", () => {
    state.permitted = false
    show()
    expect(getSystem).not.toHaveBeenCalled()
    expect(
      screen.getByRole("button", { name: "Refresh system" })
    ).toBeDisabled()
  })
  it("explains a real permission-endpoint 403 without masking other authorization outages", () => {
    state.permitted = false
    state.error = new ApiError(new Response(null, { status: 403 }), {
      detail: "The caller does not have the required role for this resource.",
    })
    show()
    expect(screen.getByRole("alert")).toHaveTextContent("Global Admin")
    expect(getSystem).not.toHaveBeenCalled()
  })
  it("hides old healthy rows and motion on a failed current snapshot, then recovers on refresh", async () => {
    show()
    await screen.findByRole("table", { name: "API and controller processes" })
    vi.mocked(getSystem).mockRejectedValue(
      new Error("System inventory unavailable")
    )
    await userEvent.click(
      screen.getByRole("button", { name: "Refresh system" })
    )
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "System inventory unavailable"
    )
    expect(screen.queryByRole("table")).toBeNull()
    expect(document.querySelector(".kardia-system-flow")).toBeNull()
    vi.mocked(getSystem).mockResolvedValue(observation)
    await userEvent.click(
      screen.getByRole("button", { name: "Refresh system" })
    )
    expect(
      await screen.findByRole("table", { name: "Workers and assigned work" })
    ).toBeVisible()
  })
  it("pauses automatic updates, labels the retained snapshot, and allows motion to be disabled independently", async () => {
    const shown = show()
    await screen.findByRole("table", { name: "API and controller processes" })
    expect(document.querySelector(".kardia-system-flow")).not.toBeNull()
    await userEvent.click(
      screen.getByRole("checkbox", { name: "Animate observed activity" })
    )
    expect(document.querySelector(".kardia-system-flow")).toBeNull()
    await userEvent.click(
      screen.getByRole("button", { name: "Pause live updates" })
    )
    expect(screen.getByRole("status")).toHaveTextContent(
      "Paused snapshot; not live"
    )
    expect(
      shown.client.getQueryCache().find({ queryKey: ["admin-system"] })?.options
    ).toMatchObject({
      enabled: false,
      refetchInterval: false,
    })
    await userEvent.click(
      screen.getByRole("button", { name: "Resume live updates" })
    )
    await waitFor(() =>
      expect(screen.getByRole("status")).toHaveTextContent(
        "Live snapshots every 2 seconds"
      )
    )
  })
  it("hides inventory when an in-flight request stalls beyond the heartbeat window", async () => {
    const shown = show()
    await screen.findByRole("table", { name: "API and controller processes" })
    const cached = shown.client.getQueryData(["admin-system"])
    await act(async () => {
      shown.client.setQueryData(["admin-system"], cached, {
        updatedAt: Date.now() - 30_000,
      })
    })
    await waitFor(() =>
      expect(screen.getByRole("alert")).toHaveTextContent(
        "No fresh system snapshot"
      )
    )
    expect(screen.queryByRole("table")).toBeNull()
  })
  it("does not turn empty inventories into fictional replicas or a leader", async () => {
    vi.mocked(getSystem).mockResolvedValue({
      ...observation,
      snapshot: { ...observation.snapshot, instances: [], workers: [] },
    })
    show()
    expect(await screen.findByText("No active controller lease")).toBeVisible()
    expect(screen.getByText("No observed application processes.")).toBeVisible()
    expect(screen.getByText("No registered workers.")).toBeVisible()
  })
  it("keeps the complete inventory when the diagram has a bounded row count", async () => {
    const many: SystemObservation = structuredClone(observation)
    many.snapshot.instances = Array.from({ length: 10 }, (_, index) => ({
      ...observation.snapshot.instances[0],
      id: index.toString(16).padStart(32, "0"),
      name: `kardia-api-${index}`,
    }))
    vi.mocked(getSystem).mockResolvedValue(many)
    show()
    expect(
      await screen.findByText(/Diagram shows the first eight/)
    ).toBeVisible()
    const inventory = screen.getByRole("region", {
      name: "Application instance inventory",
    })
    expect(within(inventory).getAllByRole("row")).toHaveLength(11)
  })
  it("does not animate unrelated live sends toward this browser", async () => {
    const current = structuredClone(observation)
    current.snapshot.instances[1].liveDeliveries++
    const frame = systemFrame(current, observation)
    const shown = show()
    await screen.findByRole("table", { name: "API and controller processes" })
    await act(async () => {
      shown.client.setQueryData(["admin-system"], frame)
    })
    expect(
      await screen.findByText("0 live connections; sends observed")
    ).toBeVisible()
    expect(document.querySelectorAll(".kardia-system-flow")).toHaveLength(3)
  })
})
