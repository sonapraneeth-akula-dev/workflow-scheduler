import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { act, render, screen, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { KubernetesMemoryView } from "./KubernetesMemoryView"
import { getKubernetesMemory, type KubernetesMemorySnapshot } from "./api"

vi.mock("./api", async (original) => ({
  ...(await original<typeof import("./api")>()),
  getKubernetesMemory: vi.fn(),
}))
const snapshot: KubernetesMemorySnapshot = {
  observedAt: "2026-10-21T15:00:00Z",
  staleAfterSeconds: 60,
  nodes: [
    {
      name: "kardia-lab-01",
      capacityBytes: 3221225472,
      allocatableBytes: 3000000000,
      workingSetBytes: 1073741824,
      sampledAt: "2026-10-21T14:59:59Z",
    },
  ],
  pods: [
    {
      name: "kardia-frontend-0",
      namespace: "kardia",
      uid: "frontend-uid",
      nodeName: "kardia-lab-01",
      role: "kardia-frontend",
      phase: "Running",
      ready: true,
      workingSetBytes: 15728640,
      sampledAt: "2026-10-21T14:59:59Z",
    },
  ],
}
function show() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  const tree = (paused = false, now = Date.now()) => (
    <QueryClientProvider client={client}>
      <KubernetesMemoryView paused={paused} now={now} />
    </QueryClientProvider>
  )
  const rendered = render(tree())
  return {
    client,
    rerender: (paused: boolean, now = Date.now()) =>
      rendered.rerender(tree(paused, now)),
  }
}

describe("measured Kubernetes node and pod RAM", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    vi.mocked(getKubernetesMemory).mockResolvedValue(structuredClone(snapshot))
  })
  it("shows node capacity, measured working set, pod placement and sample times", async () => {
    show()
    const node = await screen.findByRole("table", {
      name: "Node / VM RAM working set",
    })
    expect(within(node).getByText("1,024 MiB")).toBeVisible()
    expect(within(node).getByText("3,072 MiB")).toBeVisible()
    const pods = screen.getByRole("table", {
      name: "Application pod RAM working set",
    })
    expect(within(pods).getByText("15 MiB")).toBeVisible()
    expect(within(pods).getByText("kardia-frontend-0")).toBeVisible()
    expect(within(pods).getByText("Running / Ready")).toBeVisible()
    expect(within(pods).getByText("kardia-lab-01")).toBeVisible()
    expect(
      screen.getByText(/Missing or stale measurements are not zero/)
    ).toBeVisible()
  })
  it("never turns missing measurements into zero, and labels stale samples", async () => {
    const data = structuredClone(snapshot)
    const node = data.nodes[0]
    const pod = data.pods[0]
    if (!node || !pod)
      throw new Error("The RAM fixture requires a node and pod.")
    node.workingSetBytes = null
    node.sampledAt = null
    pod.sampledAt = "2026-10-21T14:50:00Z"
    vi.mocked(getKubernetesMemory).mockResolvedValue(data)
    show()
    expect(await screen.findByText("No RAM sample")).toBeVisible()
    expect(screen.getByText("Stale RAM sample")).toBeVisible()
    expect(screen.queryByText("0 MiB")).toBeNull()
    expect(screen.queryByText("15 MiB")).toBeNull()
  })
  it("hides old measurements on a current Metrics API failure, then recovers on explicit refresh", async () => {
    show()
    await screen.findByText("15 MiB")
    vi.mocked(getKubernetesMemory).mockRejectedValue(
      new Error("Metrics API permission denied")
    )
    await userEvent.click(screen.getByRole("button", { name: "Refresh RAM" }))
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Metrics API permission denied"
    )
    expect(screen.queryByRole("table")).toBeNull()
    vi.mocked(getKubernetesMemory).mockResolvedValue(snapshot)
    await userEvent.click(screen.getByRole("button", { name: "Refresh RAM" }))
    expect(await screen.findByText("15 MiB")).toBeVisible()
  })
  it("pauses polling with the main view and hides a stalled live snapshot after its freshness window", async () => {
    const shown = show()
    await screen.findByText("15 MiB")
    shown.rerender(true)
    expect(screen.getByText(/Paused RAM snapshot/)).toBeVisible()
    expect(
      shown.client
        .getQueryCache()
        .find({ queryKey: ["admin-system-kubernetes-memory"] })?.options
    ).toMatchObject({ enabled: false, refetchInterval: false })
    await act(async () => shown.rerender(false, Date.now() + 61000))
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "No fresh Kubernetes RAM snapshot"
    )
    expect(screen.queryByRole("table")).toBeNull()
  })
})
