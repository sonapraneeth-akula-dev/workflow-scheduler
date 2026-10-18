import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { getPermissions } from "@/lib/api/client"
import { AbortDialog } from "./AbortDialog"
import { abortRun } from "./abort"

vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  getPermissions: vi.fn(),
}))
vi.mock("./abort", async (original) => ({
  ...(await original<typeof import("./abort")>()),
  abortRun: vi.fn(),
}))
const run = {
  runId: "019ed132-2f44-7000-8000-000000000001",
  status: 1,
  abort: null,
}
const receipt = {
  by: "11111111-1111-1111-1111-111111111111",
  byName: "Original operator",
  at: "2026-10-16T08:00:00.1234567Z",
  reason: "Original reason",
  source: "UI" as const,
}
const grant = {
  scope: "run" as const,
  id: run.runId,
  viewer: true,
  author: false,
  operator: true,
  admin: false,
}
function show(props = { ...run, live: true }) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: 3, retryDelay: 0 },
    },
  })
  const view = render(
    <QueryClientProvider client={client}>
      <AbortDialog {...props} />
    </QueryClientProvider>
  )
  return { ...view, client }
}
async function open() {
  fireEvent.click(await screen.findByRole("button", { name: "Abort run" }))
  return screen.findByRole("alertdialog", { name: "Abort this run?" })
}
describe("confirmed abort controls", () => {
  beforeEach(() => vi.resetAllMocks())
  it.each([false, true])(
    "hides action for Viewer/Author without Operator (author=%s)",
    async (author) => {
      vi.mocked(getPermissions).mockResolvedValue({
        ...grant,
        operator: false,
        author,
      })
      const { client } = show()
      await waitFor(() =>
        expect(
          client.getQueryState(["permissions", "run", run.runId])?.status
        ).toBe("success")
      )
      expect(
        screen.queryByRole("button", { name: "Abort run" })
      ).not.toBeInTheDocument()
      expect(abortRun).not.toHaveBeenCalled()
    }
  )
  it("fails closed on permission read errors", async () => {
    vi.mocked(getPermissions).mockRejectedValue(
      new Error("Permissions unavailable.")
    )
    show()
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Permissions unavailable."
    )
    expect(
      screen.queryByRole("button", { name: "Abort run" })
    ).not.toBeInTheDocument()
  })
  it.each([2, 3, 4, 5, 6])(
    "hides action for nonabortable status %i",
    async (status) => {
      vi.mocked(getPermissions).mockResolvedValue(grant)
      const { client } = show({ ...run, status, live: true })
      await waitFor(() =>
        expect(
          client.getQueryState(["permissions", "run", run.runId])?.status
        ).toBe("success")
      )
      expect(
        screen.queryByRole("button", { name: "Abort run" })
      ).not.toBeInTheDocument()
    }
  )
  it("does not act on a reconnecting last-known run", async () => {
    vi.mocked(getPermissions).mockResolvedValue(grant)
    show({ ...run, live: false })
    expect(screen.getByText(/Last confirmed run status/)).toBeVisible()
    expect(
      screen.queryByRole("button", { name: "Abort run" })
    ).not.toBeInTheDocument()
  })
  it("blocks empty/whitespace/overlong reasons even through direct form submission", async () => {
    vi.mocked(getPermissions).mockResolvedValue(grant)
    show()
    const dialog = await open()
    const confirm = within(dialog).getByRole("button", {
      name: "Confirm abort",
    })
    expect(confirm).toBeDisabled()
    for (const reason of ["", " \t\n", "x".repeat(501)]) {
      fireEvent.change(within(dialog).getByLabelText("Abort reason"), {
        target: { value: reason },
      })
      fireEvent.submit(
        within(dialog).getByRole("form", { name: "Confirm run abort" })
      )
      expect(within(dialog).getByRole("alert")).toHaveTextContent("1-500")
    }
    expect(abortRun).not.toHaveBeenCalled()
    fireEvent.click(
      within(dialog).getByRole("button", { name: "Keep running" })
    )
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument()
  })
  it("requires confirmation, preserves exact reason and shows accepted rather than fabricated Aborted", async () => {
    vi.mocked(getPermissions).mockResolvedValue(grant)
    vi.mocked(abortRun).mockResolvedValue(receipt)
    const { client } = show()
    const invalidate = vi.spyOn(client, "invalidateQueries")
    const user = userEvent.setup()
    const dialog = await open()
    const reason = "  preserve reason  "
    await user.type(within(dialog).getByLabelText("Abort reason"), reason)
    expect(abortRun).not.toHaveBeenCalled()
    within(dialog).getByRole("button", { name: "Confirm abort" }).focus()
    await user.keyboard("{Enter}")
    const banner = await screen.findByRole("status")
    expect(banner).toHaveTextContent("Abort accepted")
    expect(banner).toHaveTextContent("Waiting for committed run state")
    expect(banner).toHaveTextContent("Original operator")
    expect(banner).toHaveTextContent(receipt.at)
    expect(abortRun).toHaveBeenCalledWith(run.runId, reason)
    expect(
      screen.queryByText("Aborted", { exact: true })
    ).not.toBeInTheDocument()
    expect(screen.getByText("Running", { exact: true })).toBeVisible()
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ["run-history"] })
    await waitFor(() =>
      expect(screen.getByRole("region", { name: "Run controls" })).toHaveFocus()
    )
  })
  it("renders committed Aborting and Aborted details from live authoritative state", () => {
    vi.mocked(getPermissions).mockResolvedValue(grant)
    const client = new QueryClient()
    const view = render(
      <QueryClientProvider client={client}>
        <AbortDialog {...run} status={2} abort={receipt} live />
      </QueryClientProvider>
    )
    expect(screen.getByText("Aborting", { exact: true })).toBeVisible()
    expect(screen.getByRole("status")).toHaveTextContent(
      "Cleanup may still be running"
    )
    view.rerender(
      <QueryClientProvider client={client}>
        <AbortDialog {...run} status={5} abort={receipt} live />
      </QueryClientProvider>
    )
    expect(screen.getByText("Aborted", { exact: true })).toBeVisible()
    expect(screen.getByRole("status")).toHaveTextContent("The run is Aborted")
    expect(screen.getByRole("status")).toHaveTextContent("Original reason")
  })
  it("keeps server errors and drafts visible without automatic retries", async () => {
    vi.mocked(getPermissions).mockResolvedValue(grant)
    vi.mocked(abortRun).mockRejectedValue(
      new Error("Run already completed: 409.")
    )
    show()
    const dialog = await open()
    fireEvent.change(within(dialog).getByLabelText("Abort reason"), {
      target: { value: "Keep this draft" },
    })
    fireEvent.click(
      within(dialog).getByRole("button", { name: "Confirm abort" })
    )
    expect(await screen.findByRole("alert")).toHaveTextContent("409")
    expect(within(dialog).getByLabelText("Abort reason")).toHaveValue(
      "Keep this draft"
    )
    expect(abortRun).toHaveBeenCalledTimes(1)
  })
  it("checks permission again while a dialog is open", async () => {
    vi.mocked(getPermissions).mockResolvedValue(grant)
    const { client } = show()
    const dialog = await open()
    fireEvent.change(within(dialog).getByLabelText("Abort reason"), {
      target: { value: "Reason" },
    })
    vi.mocked(getPermissions).mockResolvedValue({ ...grant, operator: false })
    await client.invalidateQueries({ queryKey: ["permissions"] })
    await waitFor(() =>
      expect(
        within(dialog).getByRole("button", { name: "Confirm abort" })
      ).toBeDisabled()
    )
    fireEvent.submit(
      within(dialog).getByRole("form", { name: "Confirm run abort" })
    )
    await waitFor(() => expect(abortRun).not.toHaveBeenCalled())
  })
})
