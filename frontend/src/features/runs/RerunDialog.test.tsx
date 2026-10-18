import type { ReactNode } from "react"
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
import { ApiError, getPermissions } from "@/lib/api/client"
import { RerunDialog } from "./RerunDialog"
import { rerunRun } from "./rerun-api"

vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  getPermissions: vi.fn(),
}))
vi.mock("./rerun-api", () => ({ rerunRun: vi.fn() }))
vi.mock("@tanstack/react-router", () => ({
  Link: ({
    params,
    children,
  }: {
    params: { id: string }
    children: ReactNode
  }) => <a href={`/runs/${encodeURIComponent(params.id)}`}>{children}</a>,
}))
const source = {
  runId: "019ed132-2f44-7000-8000-000000000001",
  versionId: "019ed132-2f44-7000-8000-000000000002",
  status: 4,
  live: true,
}
const receipt = { runId: "019ed132-2f44-7000-8000-000000000003" }
const grant = {
  scope: "run" as const,
  id: source.runId,
  viewer: true,
  author: false,
  operator: true,
  admin: false,
}
function show(props = source) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: 3, retryDelay: 0 },
    },
  })
  const content = (next: typeof source) => (
    <QueryClientProvider client={client}>
      <RerunDialog {...next} />
    </QueryClientProvider>
  )
  const view = render(content(props))
  return {
    ...view,
    client,
    update: (next: typeof source) => view.rerender(content(next)),
  }
}
async function open() {
  fireEvent.click(
    await screen.findByRole("button", { name: "Re-run from failed" })
  )
  return screen.findByRole("alertdialog", { name: "Re-run from failed nodes?" })
}
describe("confirmed idempotent rerun controls", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    vi.mocked(getPermissions).mockResolvedValue(grant)
    vi.mocked(rerunRun).mockResolvedValue(receipt)
  })
  it.each([false, true])(
    "hides mutation for Viewer/Author (author=%s)",
    async (author) => {
      vi.mocked(getPermissions).mockResolvedValue({
        ...grant,
        author,
        operator: false,
      })
      const { client } = show()
      await waitFor(() =>
        expect(
          client.getQueryState(["permissions", "run", source.runId])?.status
        ).toBe("success")
      )
      expect(
        screen.queryByRole("button", { name: "Re-run from failed" })
      ).not.toBeInTheDocument()
      expect(rerunRun).not.toHaveBeenCalled()
    }
  )
  it.each([0, 1, 2, 3])(
    "does not offer failed rerun for status %i",
    async (status) => {
      const { client } = show({ ...source, status })
      await waitFor(() =>
        expect(
          client.getQueryState(["permissions", "run", source.runId])?.status
        ).toBe("success")
      )
      expect(
        screen.queryByRole("button", { name: "Re-run from failed" })
      ).not.toBeInTheDocument()
    }
  )
  it("fails closed on permission errors and disconnected last-known data", async () => {
    vi.mocked(getPermissions).mockRejectedValue(
      new Error("Permission service unavailable.")
    )
    show({ ...source, live: false })
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Permission service unavailable."
    )
    expect(
      screen.queryByRole("button", { name: "Re-run from failed" })
    ).not.toBeInTheDocument()
  })
  it("requires explicit keyboard confirmation, captures exact source/pin and navigates only to acknowledged new identity", async () => {
    const { client } = show()
    const invalidate = vi.spyOn(client, "invalidateQueries")
    const dialog = await open()
    expect(dialog).toHaveTextContent(source.runId)
    expect(dialog).toHaveTextContent(source.versionId)
    expect(dialog).toHaveTextContent("variables and metadata")
    expect(rerunRun).not.toHaveBeenCalled()
    const user = userEvent.setup()
    within(dialog).getByRole("button", { name: "Confirm rerun" }).focus()
    await user.keyboard("{Enter}")
    expect(await within(dialog).findByRole("status")).toHaveTextContent(
      "Execution may still be queued"
    )
    expect(within(dialog).getByRole("link")).toHaveAttribute(
      "href",
      `/runs/${receipt.runId}`
    )
    expect(rerunRun).toHaveBeenCalledWith(
      source.runId,
      expect.stringMatching(/^[0-9a-f-]{36}$/)
    )
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ["run-history"] })
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ["runs"] })
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ["catalog"] })
    expect(
      within(dialog).getByRole("button", { name: "Confirm rerun" })
    ).toBeDisabled()
    fireEvent.click(within(dialog).getByRole("button", { name: "Close" }))
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Re-run from failed" })
      ).toHaveFocus()
    )
  })
  it("cancel does not reserve a key or send a request", async () => {
    show()
    const dialog = await open()
    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }))
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument()
    expect(rerunRun).not.toHaveBeenCalled()
  })
  it("blocks double submission and closing while acceptance remains uncertain", async () => {
    let complete!: (value: typeof receipt) => void
    vi.mocked(rerunRun).mockReturnValue(
      new Promise((resolve) => {
        complete = resolve
      })
    )
    show()
    const dialog = await open()
    const form = within(dialog).getByRole("form", { name: "Confirm rerun" })
    fireEvent.submit(form)
    fireEvent.submit(form)
    expect(
      await within(dialog).findByRole("button", { name: "Accepting rerun..." })
    ).toBeDisabled()
    expect(
      within(dialog).getByRole("button", { name: "Cancel" })
    ).toBeDisabled()
    await userEvent.setup().keyboard("{Escape}")
    expect(dialog).toBeVisible()
    expect(rerunRun).toHaveBeenCalledTimes(1)
    complete(receipt)
    expect(await within(dialog).findByRole("status")).toHaveTextContent(
      "Rerun accepted"
    )
    fireEvent.submit(form)
    expect(rerunRun).toHaveBeenCalledTimes(1)
  })
  it("keeps one idempotency key across uncertain errors, close/reopen and explicit retry; no automatic retries", async () => {
    vi.mocked(rerunRun).mockRejectedValueOnce(
      new Error("Connection lost after acceptance.")
    )
    show()
    let dialog = await open()
    fireEvent.submit(
      within(dialog).getByRole("form", { name: "Confirm rerun" })
    )
    expect(await within(dialog).findByRole("alert")).toHaveTextContent(
      "Connection lost"
    )
    expect(rerunRun).toHaveBeenCalledTimes(1)
    const key = vi.mocked(rerunRun).mock.calls[0]![1]
    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }))
    dialog = await open()
    fireEvent.click(within(dialog).getByRole("button", { name: "Retry rerun" }))
    expect(await within(dialog).findByRole("status")).toHaveTextContent(
      "Rerun accepted"
    )
    expect(vi.mocked(rerunRun).mock.calls).toEqual([
      [source.runId, key],
      [source.runId, key],
    ])
  })
  it("blocks stale permission refetch, revocation and direct form submission while open", async () => {
    const { client } = show()
    const dialog = await open()
    vi.mocked(getPermissions).mockResolvedValue({ ...grant, operator: false })
    await client.invalidateQueries({ queryKey: ["permissions"] })
    await waitFor(() =>
      expect(
        within(dialog).getByRole("button", { name: "Confirm rerun" })
      ).toBeDisabled()
    )
    fireEvent.submit(
      within(dialog).getByRole("form", { name: "Confirm rerun" })
    )
    expect(rerunRun).not.toHaveBeenCalled()
    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }))
    await waitFor(() =>
      expect(
        screen.getByRole("region", { name: "Rerun controls" })
      ).toHaveFocus()
    )
  })
  it("refreshes permissions after authoritative 403; never displays an accepted receipt", async () => {
    vi.mocked(rerunRun).mockRejectedValue(
      new ApiError(new Response(null, { status: 403 }), {
        title: "Permission revoked",
      })
    )
    const { client } = show()
    const dialog = await open()
    vi.mocked(getPermissions).mockResolvedValue({ ...grant, operator: false })
    const invalidate = vi.spyOn(client, "invalidateQueries")
    fireEvent.submit(
      within(dialog).getByRole("form", { name: "Confirm rerun" })
    )
    expect(await within(dialog).findByRole("alert")).toHaveTextContent(
      "Permission revoked"
    )
    await waitFor(() =>
      expect(invalidate).toHaveBeenCalledWith({ queryKey: ["permissions"] })
    )
    expect(within(dialog).queryByRole("link")).not.toBeInTheDocument()
  })
  it("blocks confirmation if live state disconnects while dialog is open", async () => {
    const { update } = show()
    const dialog = await open()
    update({ ...source, live: false })
    expect(
      within(dialog).getByRole("button", { name: "Confirm rerun" })
    ).toBeDisabled()
    fireEvent.submit(
      within(dialog).getByRole("form", { name: "Confirm rerun" })
    )
    expect(rerunRun).not.toHaveBeenCalled()
  })
  it("fails closed during a permission refetch, even with a cached Operator grant", async () => {
    const { client } = show()
    const dialog = await open()
    let finish!: (value: typeof grant) => void
    vi.mocked(getPermissions).mockReturnValueOnce(
      new Promise((resolve) => {
        finish = resolve
      })
    )
    const refresh = client.invalidateQueries({ queryKey: ["permissions"] })
    await waitFor(() =>
      expect(
        within(dialog).getByRole("button", { name: "Confirm rerun" })
      ).toBeDisabled()
    )
    fireEvent.submit(
      within(dialog).getByRole("form", { name: "Confirm rerun" })
    )
    expect(rerunRun).not.toHaveBeenCalled()
    finish({ ...grant, operator: false })
    await refresh
  })
  it("requires an exact owning-run permission response", async () => {
    vi.mocked(getPermissions).mockResolvedValue({ ...grant, id: receipt.runId })
    const { client } = show()
    await waitFor(() =>
      expect(
        client.getQueryState(["permissions", "run", source.runId])?.status
      ).toBe("success")
    )
    expect(
      screen.queryByRole("button", { name: "Re-run from failed" })
    ).not.toBeInTheDocument()
    expect(rerunRun).not.toHaveBeenCalled()
  })
})
