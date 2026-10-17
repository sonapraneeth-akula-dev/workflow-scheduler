import type { AnchorHTMLAttributes } from "react"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { fireEvent, render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { MachineFilters, MachineList } from "./MachineList"
import { getMachines, patchMachine, type Machine } from "./api"
import type { LiveSnapshot } from "@/lib/live/subscriptions"

const state = vi.hoisted(() => ({
  permitted: true,
  checking: false,
  retry: vi.fn(),
  snapshot: undefined as LiveSnapshot | undefined,
}))
vi.mock("@/lib/auth/usePermissions", () => ({
  usePermissions: () => ({
    can: () => state.permitted && !state.checking,
    isSuccess: true,
    data: { viewer: state.permitted },
    error: null,
  }),
}))
vi.mock("@/lib/live/useLive", () => ({
  useLive: () => ({ snapshot: state.snapshot, retry: state.retry }),
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
  getMachines: vi.fn(),
  patchMachine: vi.fn(),
}))
const machine: Machine = {
  id: "12345678-1234-4234-9234-123456789abc",
  name: "worker <script>",
  os: "windows",
  arch: "x64",
  cores: 8,
  ramMb: 8192,
  maxTasks: 4,
  slotsUsed: 2,
  pool: "main",
  labels: { region: "eu" },
  status: "Healthy",
  lastHeartbeatAt: "2026-10-17T00:00:00Z",
  version: "1.2.3",
  cpuPercent: 20,
  ramUsedMb: 1024,
  diskFreeMb: 4096,
  currentReservation: {
    id: "22345678-1234-4234-9234-123456789abc",
    runId: "32345678-1234-4234-9234-123456789abc",
    nodeKey: "work",
    epoch: 7,
    expiresAt: "2026-10-16T00:00:00Z",
    releasePending: true,
  },
}
function show(id?: string) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  const tree = () => (
    <QueryClientProvider client={client}>
      <MachineList id={id} />
    </QueryClientProvider>
  )
  const result = render(tree())
  return { client, rerender: () => result.rerender(tree()) }
}
describe("machine list and editor", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    state.permitted = true
    state.checking = false
    state.snapshot = {
      resource: { scope: "machines", id: "*" },
      status: "live",
      error: null,
      delta: null,
      resyncVersion: 0,
      reportVersion: 0,
    }
    vi.mocked(getMachines).mockResolvedValue([machine])
    vi.mocked(patchMachine).mockResolvedValue()
  })
  it("renders fleet OS, labels, pool, health, slots and a still-held expired reservation, with safe detail links", async () => {
    show()
    expect(
      await screen.findByRole("link", { name: machine.name })
    ).toHaveAttribute("href", `/machines/${machine.id}`)
    expect(screen.getByText("windows / x64")).toBeVisible()
    expect(screen.getByText("region=eu")).toBeVisible()
    expect(screen.getByText("main")).toBeVisible()
    expect(screen.getByText("Healthy")).toBeVisible()
    expect(screen.getByText(/8 cores; 8192 MiB RAM; 2 \/ 4/)).toBeVisible()
    expect(
      screen.getByText("Release pending physical acknowledgement")
    ).toBeVisible()
    expect(
      screen.getByText("Lease expiry alone does not release this machine.")
    ).toBeVisible()
    expect(document.querySelector("script")).toBeNull()
  })
  it("uses push invalidation without scheduled inventory polling, and refresh retries live transport", async () => {
    const shown = show()
    await screen.findByText("region=eu")
    const count = vi.mocked(getMachines).mock.calls.length
    vi.mocked(getMachines).mockResolvedValue([
      { ...machine, pool: "changed", slotsUsed: 3 },
    ])
    state.snapshot = { ...state.snapshot!, resyncVersion: 1 }
    shown.rerender()
    expect(await screen.findByText("changed")).toBeVisible()
    expect(vi.mocked(getMachines).mock.calls.length).toBeGreaterThan(count)
    expect(
      shown.client
        .getQueryCache()
        .getAll()
        .find((q) => q.queryKey[0] === "machines")?.options
    ).not.toHaveProperty("refetchInterval")
    await userEvent.click(
      screen.getByRole("button", { name: "Refresh machines" })
    )
    expect(state.retry).toHaveBeenCalled()
  })
  it("hides cached inventory and edit controls on current permission denial", async () => {
    const shown = show(machine.id)
    await screen.findByRole("button", { name: "Edit labels and pool" })
    state.permitted = false
    shown.rerender()
    expect(screen.getByRole("alert")).toHaveTextContent("Global Admin")
    expect(screen.queryByText(machine.name)).toBeNull()
    expect(
      screen.queryByRole("button", { name: "Edit labels and pool" })
    ).toBeNull()
  })
  it("hides cached inventory on transport protocol failure or failed current refresh", async () => {
    const shown = show()
    await screen.findByText("region=eu")
    state.snapshot = {
      ...state.snapshot!,
      status: "error",
      error: new Error("Invalid live message"),
    }
    shown.rerender()
    expect(screen.getByRole("alert")).toHaveTextContent("Invalid live message")
    expect(screen.queryByRole("table")).toBeNull()
    state.snapshot = { ...state.snapshot!, status: "live", error: null }
    vi.mocked(getMachines).mockRejectedValue(new Error("HTTP 403"))
    shown.rerender()
    await shown.client.invalidateQueries({ queryKey: ["machines"] })
    expect(await screen.findByText("HTTP 403")).toBeVisible()
    expect(screen.queryByRole("table")).toBeNull()
  })
  it("distinguishes empty inventory from missing detail and retains a disconnected stale-data warning", async () => {
    vi.mocked(getMachines).mockResolvedValue([])
    state.snapshot = { ...state.snapshot!, status: "reconnecting" }
    show(machine.id)
    expect(
      await screen.findByText("Machine not found in the current inventory.")
    ).toBeVisible()
    expect(screen.getByText(/displayed data may be stale/)).toBeVisible()
  })
  it("edits only changed pool, preserves labels and refreshes committed inventory", async () => {
    const shown = show(machine.id)
    await userEvent.click(
      await screen.findByRole("button", { name: "Edit labels and pool" })
    )
    const dialog = screen.getByRole("alertdialog")
    expect(dialog).toHaveAccessibleName("Edit labels and pool")
    fireEvent.change(screen.getByLabelText("Pool", { exact: true }), {
      target: { value: "next" },
    })
    await userEvent.click(screen.getByRole("button", { name: "Save metadata" }))
    await waitFor(() =>
      expect(patchMachine).toHaveBeenCalledWith(machine.id, { pool: "next" })
    )
    await waitFor(() => expect(screen.queryByRole("alertdialog")).toBeNull())
    expect(shown.client.getQueryData(["machines", {}])).toEqual([machine])
  })
  it("clears labels explicitly without touching pool", async () => {
    show(machine.id)
    await userEvent.click(
      await screen.findByRole("button", { name: "Edit labels and pool" })
    )
    fireEvent.change(screen.getByLabelText("Labels (one key=value per line)"), {
      target: { value: "" },
    })
    await userEvent.click(screen.getByRole("button", { name: "Save metadata" }))
    await waitFor(() =>
      expect(patchMachine).toHaveBeenCalledWith(machine.id, { labels: {} })
    )
  })
  it("retains entered metadata after validation or server failure and never closes on failure", async () => {
    show(machine.id)
    await userEvent.click(
      await screen.findByRole("button", { name: "Edit labels and pool" })
    )
    const labels = screen.getByLabelText("Labels (one key=value per line)")
    fireEvent.change(labels, { target: { value: "region=eu\nregion=us" } })
    await userEvent.click(screen.getByRole("button", { name: "Save metadata" }))
    expect(screen.getByRole("alert")).toHaveTextContent("unique")
    expect(patchMachine).not.toHaveBeenCalled()
    fireEvent.change(labels, { target: { value: "region=us" } })
    vi.mocked(patchMachine).mockRejectedValue(new Error("Metadata unavailable"))
    await userEvent.click(screen.getByRole("button", { name: "Save metadata" }))
    expect(await screen.findByText("Metadata unavailable")).toBeVisible()
    expect(labels).toHaveValue("region=us")
    expect(screen.getByRole("alertdialog")).toBeVisible()
  })
  it("prevents unchanged saves and cancels without writing", async () => {
    show(machine.id)
    await userEvent.click(
      await screen.findByRole("button", { name: "Edit labels and pool" })
    )
    await userEvent.click(screen.getByRole("button", { name: "Save metadata" }))
    expect(screen.getByRole("alert")).toHaveTextContent("Change labels or pool")
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }))
    expect(patchMachine).not.toHaveBeenCalled()
  })
  it("retains the editor draft during permission revalidation, disables writes, and hides it on denial", async () => {
    const shown = show(machine.id)
    await userEvent.click(
      await screen.findByRole("button", { name: "Edit labels and pool" })
    )
    fireEvent.change(screen.getByLabelText("Pool", { exact: true }), {
      target: { value: "unsaved" },
    })
    state.checking = true
    shown.rerender()
    expect(screen.getByRole("alertdialog")).toBeVisible()
    expect(screen.getByLabelText("Pool", { exact: true })).toHaveValue(
      "unsaved"
    )
    expect(screen.getByRole("button", { name: "Save metadata" })).toBeDisabled()
    state.checking = false
    shown.rerender()
    expect(screen.getByLabelText("Pool", { exact: true })).toHaveValue(
      "unsaved"
    )
    expect(screen.getByRole("button", { name: "Save metadata" })).toBeEnabled()
    state.permitted = false
    shown.rerender()
    expect(screen.queryByRole("alertdialog")).toBeNull()
    expect(patchMachine).not.toHaveBeenCalled()
  })
  it("submits exact AND filters and clears all filters", async () => {
    const change = vi.fn()
    render(<MachineFilters search={{}} onChange={change} />)
    fireEvent.change(screen.getByLabelText("Pool (exact)"), {
      target: { value: "main" },
    })
    fireEvent.change(screen.getByLabelText("OS", { exact: true }), {
      target: { value: "windows" },
    })
    fireEvent.change(screen.getByLabelText("Health"), {
      target: { value: "Healthy" },
    })
    fireEvent.change(
      screen.getByLabelText("Label filters (one key=value per line)"),
      { target: { value: "region=eu\nkind=gpu" } }
    )
    await userEvent.click(
      screen.getByRole("button", { name: "Filter machines" })
    )
    expect(change).toHaveBeenCalledWith({
      pool: "main",
      os: "windows",
      status: "Healthy",
      label: ["region=eu", "kind=gpu"],
    })
    await userEvent.click(
      screen.getByRole("button", { name: "Clear machine filters" })
    )
    expect(change).toHaveBeenLastCalledWith({})
  })
})
