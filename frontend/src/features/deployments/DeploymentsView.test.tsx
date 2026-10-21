import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { act, fireEvent, render, screen, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { DeploymentsView } from "./DeploymentsView"
import { getDeploymentHistory, getDeploymentSnapshot } from "./api"
import { snapshot, history } from "./testFixture"

const access = vi.hoisted(() => ({
  permitted: true,
  checking: false,
  error: null as Error | null,
}))
vi.mock("@/lib/auth/usePermissions", () => ({
  usePermissions: () => ({
    can: () => access.permitted && !access.checking,
    isSuccess: !access.error,
    data: { admin: access.permitted },
    error: access.error,
  }),
}))
vi.mock("./api", () => ({
  getDeploymentSnapshot: vi.fn(),
  getDeploymentHistory: vi.fn(),
}))
function show() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  const tree = () => (
    <QueryClientProvider client={client}>
      <DeploymentsView />
    </QueryClientProvider>
  )
  const rendered = render(tree())
  return { client, ...rendered, rerender: () => rendered.rerender(tree()) }
}
describe("deployment transparency", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    access.permitted = true
    access.checking = false
    access.error = null
    vi.mocked(getDeploymentSnapshot).mockResolvedValue(snapshot())
    vi.mocked(getDeploymentHistory).mockResolvedValue(history())
  })
  afterEach(() => vi.useRealTimers())
  it("separates staged targets from actual images and displays ordered reported steps as plain text", async () => {
    show()
    expect(await screen.findByText("Awaiting replacement")).toBeVisible()
    expect(
      screen.getByText(/Ready does not mean the new version/)
    ).toBeVisible()
    const worker = screen.getByRole("article", { name: "kardia-worker" })
    expect(worker).toHaveTextContent("Updated 0")
    expect(worker).toHaveTextContent("Matching target images 0")
    await userEvent.click(screen.getByText("kardia-worker-old"))
    expect(screen.getByText("Image: kardia:ha-lab-old")).toBeVisible()
    expect(screen.getByText("Image ID: sha256:old-image")).toBeVisible()
    expect(screen.getByText(/CrashLoopBackOff/)).toHaveTextContent("Restarts 4")
    expect(screen.getByText("kardia-migrate-new: Succeeded")).toBeVisible()
    const recorded = screen.getByRole("region", {
      name: "Recorded deployment activity",
    })
    expect(recorded).toHaveTextContent("Reported Succeeded")
    const events = within(recorded).getAllByRole("listitem")
    expect(events[0]).toHaveTextContent("1. Build")
    expect(events[1]).toHaveTextContent("2. Complete")
    expect(recorded).toHaveTextContent("Build <script>alert(1)</script>")
    expect(document.querySelector("script")).toBeNull()
  })
  it("does not query or reveal cached rows for denied or revalidating callers", async () => {
    access.permitted = false
    const shown = show()
    expect(getDeploymentSnapshot).not.toHaveBeenCalled()
    expect(getDeploymentHistory).not.toHaveBeenCalled()
    expect(screen.getByRole("alert")).toHaveTextContent("Global Admin")
    expect(
      screen.getByRole("button", { name: "Refresh deployments" })
    ).toBeDisabled()
    access.permitted = true
    shown.rerender()
    await screen.findByText("Awaiting replacement")
    access.checking = true
    shown.rerender()
    expect(screen.queryByText("Awaiting replacement")).toBeNull()
    expect(screen.queryByRole("article")).toBeNull()
    access.error = new Error("Permission service unavailable")
    shown.rerender()
    expect(screen.getByRole("alert")).toHaveTextContent(
      "Permission service unavailable"
    )
  })
  it("keeps history visible when observation fails, hiding old rollout state until recovery", async () => {
    show()
    await screen.findByText("Awaiting replacement")
    vi.mocked(getDeploymentSnapshot).mockRejectedValue(
      new Error("Kubernetes unavailable")
    )
    await userEvent.click(
      screen.getByRole("button", { name: "Refresh deployments" })
    )
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Kubernetes unavailable"
    )
    expect(screen.queryByText("Awaiting replacement")).toBeNull()
    expect(screen.getByText("Reported Succeeded")).toBeVisible()
    vi.mocked(getDeploymentSnapshot).mockResolvedValue(snapshot())
    await userEvent.click(
      screen.getByRole("button", { name: "Refresh deployments" })
    )
    expect(await screen.findByText("Awaiting replacement")).toBeVisible()
  })
  it("does not equate empty records with no deployments or a silent Running reporter with success", async () => {
    const reported = history()
    const operation = reported.operations[0]!
    operation.status = "Running"
    operation.events = []
    operation.sequence = 0
    operation.lastReportedAt = new Date(Date.now() - 601_000).toISOString()
    vi.mocked(getDeploymentHistory).mockResolvedValue(reported)
    show()
    expect(
      await screen.findByText("Unconfirmed; last reported Running")
    ).toBeVisible()
    expect(screen.queryByText("Reported Succeeded")).toBeNull()
    vi.mocked(getDeploymentHistory).mockResolvedValue({
      ...reported,
      operations: [],
    })
    await userEvent.click(
      screen.getByRole("button", { name: "Refresh deployments" })
    )
    expect(
      await screen.findByText(/No recorded deployment activity/)
    ).toHaveTextContent("does not mean no deployments occurred")
  })
  it("pauses polling but expires old state and supports manual refresh while paused", async () => {
    vi.useFakeTimers()
    const shown = show()
    await act(() => vi.advanceTimersByTimeAsync(10))
    expect(screen.getByText("Awaiting replacement")).toBeVisible()
    fireEvent.click(
      screen.getByRole("button", { name: "Pause deployment updates" })
    )
    await act(() => vi.advanceTimersByTimeAsync(21_000))
    expect(getDeploymentSnapshot).toHaveBeenCalledTimes(1)
    expect(getDeploymentHistory).toHaveBeenCalledTimes(1)
    expect(screen.getByText(/Rollout observation is stale/)).toBeVisible()
    expect(screen.getByText(/Activity history is stale/)).toBeVisible()
    expect(screen.queryByText("Awaiting replacement")).toBeNull()
    fireEvent.click(
      screen.getByRole("button", { name: "Refresh deployments" })
    )
    await act(() => vi.advanceTimersByTimeAsync(10))
    expect(screen.getByText("Awaiting replacement")).toBeVisible()
    expect(screen.getByText("Paused snapshots; not live")).toBeVisible()
    shown.unmount()
  })
})
