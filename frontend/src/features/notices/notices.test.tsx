import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { act, render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { SystemNoticeBar } from "./SystemNoticeBar"
import { SystemNoticeEditor } from "./SystemNoticeEditor"
import {
  changeSystemNotice,
  getSystemNotice,
  noticeQueryKey,
  type SystemNoticeSnapshot,
} from "./api"

vi.mock("./api", async (original) => ({
  ...(await original<typeof import("./api")>()),
  getSystemNotice: vi.fn(),
  changeSystemNotice: vi.fn(),
}))
const snapshot = (canManage = true): SystemNoticeSnapshot => ({
  revision: "1",
  notice: {
    message: "Maintenance in progress",
    severity: "Warning",
    expiresAt: null,
  },
  canManage,
})
const clients: QueryClient[] = []
function show(editor = false) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  clients.push(client)
  render(
    <QueryClientProvider client={client}>
      <SystemNoticeBar />
      {editor ? <SystemNoticeEditor /> : null}
    </QueryClientProvider>
  )
  return client
}
beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(getSystemNotice).mockResolvedValue(snapshot())
})
afterEach(() => {
  clients.splice(0).forEach((client) => client.clear())
  vi.useRealTimers()
})

describe("persistent top notice", () => {
  it("uses polite status for information/warning, assertive alert only for critical, and never adds dismissal", async () => {
    const client = show()
    expect(await screen.findByText("Maintenance in progress")).toBeVisible()
    expect(screen.getByRole("status")).toHaveTextContent(
      "Warning system notice"
    )
    expect(screen.queryByRole("button", { name: /dismiss|close/i })).toBeNull()
    await act(async () =>
      client.setQueryData(noticeQueryKey, {
        ...snapshot(),
        notice: { ...snapshot().notice, severity: "Critical" },
      })
    )
    await waitFor(() =>
      expect(screen.getByRole("alert")).toHaveTextContent(
        "Maintenance in progress"
      )
    )
  })
  it("renders markup as text, not HTML or clickable links", async () => {
    vi.mocked(getSystemNotice).mockResolvedValue({
      ...snapshot(),
      notice: {
        message:
          '<script>alert(1)</script><a href="https://example.com">link</a>',
        severity: "Info",
        expiresAt: null,
      },
    })
    show()
    expect(await screen.findByText(/<script>alert/)).toBeVisible()
    expect(document.querySelector("script, a")).toBeNull()
  })
  it("shows no fabricated notice after clear or expiry from the API", async () => {
    vi.mocked(getSystemNotice).mockResolvedValue({
      ...snapshot(),
      notice: null,
    })
    show()
    await act(async () => {})
    expect(screen.queryByText(/system notice$/i)).toBeNull()
  })
  it("expires locally even when a subsequent read cannot complete", async () => {
    vi.useFakeTimers()
    vi.mocked(getSystemNotice).mockResolvedValue({
      ...snapshot(),
      notice: {
        ...snapshot().notice!,
        expiresAt: new Date(Date.now() + 5000).toISOString(),
      },
    })
    show()
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1)
    })
    expect(screen.getByText("Maintenance in progress")).toBeVisible()
    await act(async () => {
      await vi.advanceTimersByTimeAsync(5001)
    })
    expect(screen.queryByText("Maintenance in progress")).toBeNull()
  })
  it("keeps an unexpired last notice but labels a refresh failure and recovers explicitly", async () => {
    const client = show()
    await screen.findByText("Maintenance in progress")
    vi.mocked(getSystemNotice).mockRejectedValue(new Error("API unavailable"))
    await act(async () => {
      await client.invalidateQueries({ queryKey: noticeQueryKey })
    })
    expect(
      await screen.findByText(/Showing the last received notice/)
    ).toBeVisible()
    expect(screen.getByText("Maintenance in progress")).toBeVisible()
    vi.mocked(getSystemNotice).mockResolvedValue({
      ...snapshot(),
      notice: null,
    })
    await userEvent.click(screen.getByRole("button", { name: "Retry notices" }))
    expect(screen.queryByText("Maintenance in progress")).toBeNull()
  })
  it("failed initial read is unknown, never an all-clear", async () => {
    vi.mocked(getSystemNotice).mockRejectedValue(
      new Error("Storage unavailable")
    )
    show()
    expect(
      await screen.findByText(/Current notice status is unknown/)
    ).toBeVisible()
  })
  it("labels stalled snapshots after 90 seconds without discarding their active notice", async () => {
    vi.useFakeTimers()
    const unresolved = new Promise<SystemNoticeSnapshot>(() => {})
    vi.mocked(getSystemNotice)
      .mockResolvedValueOnce(snapshot())
      .mockReturnValue(unresolved)
    show()
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1)
    })
    await act(async () => {
      await vi.advanceTimersByTimeAsync(90_001)
    })
    expect(screen.getByText(/Showing the last received notice/)).toBeVisible()
    expect(screen.getByText("Maintenance in progress")).toBeVisible()
  })
})

describe("Global Admin notice editor", () => {
  it("is hidden for ordinary users including team administrators", async () => {
    vi.mocked(getSystemNotice).mockResolvedValue(snapshot(false))
    show(true)
    await screen.findByText("Maintenance in progress")
    expect(screen.queryByRole("heading", { name: "System notice" })).toBeNull()
  })
  it("publishes against the pinned revision and updates the shared banner immediately", async () => {
    vi.mocked(changeSystemNotice).mockResolvedValue({
      ...snapshot(),
      revision: "2",
      notice: {
        message: "Service restored",
        severity: "Info",
        expiresAt: null,
      },
    })
    show(true)
    await userEvent.click(
      await screen.findByRole("button", { name: "Edit system notice" })
    )
    await userEvent.clear(screen.getByLabelText("Message"))
    await userEvent.type(screen.getByLabelText("Message"), "Service restored")
    await userEvent.selectOptions(screen.getByLabelText("Severity"), "Info")
    await userEvent.click(
      screen.getByRole("button", { name: "Publish notice" })
    )
    expect(changeSystemNotice).toHaveBeenCalledWith("1", {
      message: "Service restored",
      severity: "Info",
      expiresAt: null,
    })
    expect(await screen.findByText("System notice published.")).toBeVisible()
    expect(screen.getByText("Service restored")).toBeVisible()
  })
  it("preserves an in-progress draft and old revision through background updates, then explicitly reloads only its revision", async () => {
    const client = show(true)
    await userEvent.click(
      await screen.findByRole("button", { name: "Edit system notice" })
    )
    await userEvent.clear(screen.getByLabelText("Message"))
    await userEvent.type(screen.getByLabelText("Message"), "My draft")
    vi.mocked(getSystemNotice).mockResolvedValue({
      ...snapshot(),
      revision: "2",
    })
    await act(async () => {
      await client.invalidateQueries({ queryKey: noticeQueryKey })
    })
    expect(screen.getByLabelText("Message")).toHaveValue("My draft")
    expect(
      await screen.findByText(/Another administrator changed/)
    ).toBeVisible()
    vi.mocked(changeSystemNotice).mockRejectedValue(
      new Error("This system notice changed elsewhere")
    )
    await userEvent.click(
      screen.getByRole("button", { name: "Publish notice" })
    )
    expect(changeSystemNotice).toHaveBeenCalledWith("1", {
      message: "My draft",
      severity: "Warning",
      expiresAt: null,
    })
    expect(
      await screen.findByRole("button", { name: "Publish notice" })
    ).toBeDisabled()
    await userEvent.click(
      screen.getByRole("button", { name: "Reload latest notice (keep draft)" })
    )
    expect(screen.getByLabelText("Message")).toHaveValue("My draft")
    vi.mocked(changeSystemNotice).mockResolvedValue({
      ...snapshot(),
      revision: "3",
    })
    await userEvent.click(
      screen.getByRole("button", { name: "Publish notice" })
    )
    expect(changeSystemNotice).toHaveBeenLastCalledWith("2", {
      message: "My draft",
      severity: "Warning",
      expiresAt: null,
    })
  })
  it("requires explicit confirmation to clear the notice", async () => {
    vi.mocked(changeSystemNotice).mockResolvedValue({
      ...snapshot(),
      revision: "2",
      notice: null,
    })
    show(true)
    await userEvent.click(
      await screen.findByRole("button", { name: "Edit system notice" })
    )
    await userEvent.click(screen.getByRole("button", { name: "Clear notice" }))
    expect(changeSystemNotice).not.toHaveBeenCalled()
    await userEvent.click(screen.getByRole("button", { name: "Confirm clear" }))
    expect(changeSystemNotice).toHaveBeenCalledWith("1", null)
    expect(await screen.findByText("System notice cleared.")).toBeVisible()
    expect(screen.queryByText("Maintenance in progress")).toBeNull()
  })
})
