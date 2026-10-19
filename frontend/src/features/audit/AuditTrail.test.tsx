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
import { ApiError } from "@/lib/api/client"
import { AuditFilters, AuditTrail } from "./AuditTrail"
import { getAudit, type AuditRow } from "./api"

vi.mock("./api", async (original) => ({
  ...(await original<typeof import("./api")>()),
  getAudit: vi.fn(),
}))
const row: AuditRow = {
  id: "9223372036854775807",
  at: "2026-10-17T00:00:00.000001Z",
  actor: "admin-user",
  actorKind: "User",
  action: "machine.update",
  targetKind: "Machine",
  targetId: "machine-one",
  teamId: null,
  summary: {
    before: { pool: "<script>old</script>" },
    after: { pool: "new & safe" },
  },
}
function show() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  render(
    <QueryClientProvider client={client}>
      <AuditTrail search={{}} />
    </QueryClientProvider>
  )
  return client
}
describe("audit trail", () => {
  beforeEach(() => vi.resetAllMocks())
  it("renders committed actor/action/target/time/before-after safely and only loads more on request", async () => {
    vi.mocked(getAudit)
      .mockResolvedValueOnce({ items: [row], nextCursor: "cursor" })
      .mockResolvedValueOnce({
        items: [{ ...row, id: "9223372036854775806" }],
        nextCursor: null,
      })
    show()
    expect(await screen.findByText("admin-user")).toBeVisible()
    expect(screen.getByText("machine.update")).toBeVisible()
    expect(screen.getByText("machine-one")).toBeVisible()
    expect(screen.getByText("#9223372036854775807")).toBeVisible()
    expect(screen.queryByText(/<script>old<\/script>/)).toBeNull()
    expect(screen.queryByRole("dialog")).toBeNull()
    const trigger = screen.getByRole("button", {
      name: `View summary for audit record #${row.id}`,
    })
    await userEvent.click(trigger)
    const dialog = screen.getByRole("dialog", {
      name: `Audit record #${row.id}`,
    })
    expect(
      within(dialog).getByRole("region", { name: "Audit summary JSON" })
        .textContent
    ).toBe(JSON.stringify(row.summary, null, 2))
    expect(document.querySelector("script")).toBeNull()
    await userEvent.click(
      within(dialog).getByRole("button", { name: "Close summary" })
    )
    expect(screen.queryByRole("dialog")).toBeNull()
    expect(trigger).toHaveFocus()
    expect(getAudit).toHaveBeenCalledTimes(1)
    await userEvent.click(
      screen.getByRole("button", { name: "Load more audit records" })
    )
    expect(await screen.findByText("#9223372036854775806")).toBeVisible()
    expect(vi.mocked(getAudit).mock.calls[1]?.[1]).toEqual({
      cursor: "cursor",
      after: row,
    })
    expect(
      screen.queryByRole("button", { name: "Load more audit records" })
    ).toBeNull()
  })
  it("hides all cached audit data when current authorization is revoked", async () => {
    vi.mocked(getAudit).mockResolvedValueOnce({
      items: [row],
      nextCursor: "cursor",
    })
    const client = show()
    await screen.findByText("admin-user")
    await userEvent.click(
      screen.getByRole("button", {
        name: `View summary for audit record #${row.id}`,
      })
    )
    expect(screen.getByRole("dialog")).toBeVisible()
    vi.mocked(getAudit).mockRejectedValue(
      new ApiError(new Response(null, { status: 403 }), undefined)
    )
    await client.invalidateQueries({ queryKey: ["audit"] })
    expect(await screen.findByRole("alert")).toHaveTextContent("403")
    expect(screen.queryByText("admin-user")).toBeNull()
    expect(screen.queryByRole("table")).toBeNull()
    expect(screen.queryByRole("dialog")).toBeNull()
    expect(screen.queryByText(/<script>old<\/script>/)).toBeNull()
    expect(
      screen.queryByRole("button", { name: "Load more audit records" })
    ).toBeNull()
  })
  it("opens the selected row only, traps keyboard focus and restores it on Escape", async () => {
    const second = {
      ...row,
      id: "9223372036854775806",
      summary: { reason: "second-row-only" },
    }
    vi.mocked(getAudit).mockResolvedValueOnce({
      items: [row, second],
      nextCursor: null,
    })
    show()
    const user = userEvent.setup()
    const trigger = await screen.findByRole("button", {
      name: `View summary for audit record #${second.id}`,
    })
    trigger.focus()
    await user.keyboard("{Enter}")
    const dialog = screen.getByRole("dialog", {
      name: `Audit record #${second.id}`,
    })
    expect(
      within(dialog).getByRole("region", { name: "Audit summary JSON" })
        .textContent
    ).toBe(JSON.stringify(second.summary, null, 2))
    expect(screen.queryByText(/<script>old<\/script>/)).toBeNull()
    const close = within(dialog).getByRole("button", { name: "Close summary" })
    close.focus()
    await user.tab()
    expect(
      within(dialog).getByRole("region", { name: "Audit summary JSON" })
    ).toHaveFocus()
    await user.keyboard("{Escape}")
    expect(screen.queryByRole("dialog")).toBeNull()
    expect(trigger).toHaveFocus()
  })
  it("closes an open summary on storage failure rather than leaking cached metadata", async () => {
    vi.mocked(getAudit).mockResolvedValueOnce({
      items: [row],
      nextCursor: null,
    })
    const client = show()
    await userEvent.click(
      await screen.findByRole("button", {
        name: `View summary for audit record #${row.id}`,
      })
    )
    vi.mocked(getAudit).mockRejectedValueOnce(new Error("Audit unavailable"))
    await client.invalidateQueries({ queryKey: ["audit"] })
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Audit unavailable"
    )
    expect(screen.queryByRole("dialog")).toBeNull()
    expect(screen.queryByRole("table")).toBeNull()
  })
  it("distinguishes an empty result from loading and unavailable", async () => {
    vi.mocked(getAudit).mockResolvedValueOnce({ items: [], nextCursor: null })
    show()
    expect(await screen.findByText("No matching audit records.")).toBeVisible()
    vi.mocked(getAudit).mockRejectedValue(new Error("Audit trail unavailable"))
    await userEvent.click(screen.getByRole("button", { name: "Refresh audit" }))
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Audit trail unavailable"
    )
    expect(screen.queryByText("No matching audit records.")).toBeNull()
  })
  it("uses exact actor/target and inclusive/exclusive UTC dates; clear removes filters", async () => {
    const change = vi.fn()
    render(<AuditFilters search={{}} onChange={change} />)
    fireEvent.change(screen.getByLabelText("Actor (exact)"), {
      target: { value: "admin-user" },
    })
    fireEvent.change(screen.getByLabelText("Target kind (exact)"), {
      target: { value: "Machine" },
    })
    fireEvent.change(screen.getByLabelText("Target ID (exact)"), {
      target: { value: "machine-one" },
    })
    fireEvent.change(screen.getByLabelText("From date (UTC, inclusive)"), {
      target: { value: "2026-10-17" },
    })
    fireEvent.change(screen.getByLabelText("Until date (UTC, exclusive)"), {
      target: { value: "2026-10-18" },
    })
    await userEvent.click(screen.getByRole("button", { name: "Filter audit" }))
    expect(change).toHaveBeenCalledWith({
      actor: "admin-user",
      targetKind: "Machine",
      targetId: "machine-one",
      from: "2026-10-17T00:00:00Z",
      to: "2026-10-18T00:00:00Z",
    })
    await userEvent.click(
      screen.getByRole("button", { name: "Clear audit filters" })
    )
    expect(change).toHaveBeenLastCalledWith({})
  })
  it("rejects reversed dates without discarding form values or submitting", async () => {
    const change = vi.fn()
    render(<AuditFilters search={{ actor: "retained" }} onChange={change} />)
    fireEvent.change(screen.getByLabelText("From date (UTC, inclusive)"), {
      target: { value: "2026-10-18" },
    })
    fireEvent.change(screen.getByLabelText("Until date (UTC, exclusive)"), {
      target: { value: "2026-10-17" },
    })
    await userEvent.click(screen.getByRole("button", { name: "Filter audit" }))
    expect(screen.getByRole("alert")).toHaveTextContent("exclusive end")
    expect(screen.getByLabelText("Actor (exact)")).toHaveValue("retained")
    expect(change).not.toHaveBeenCalled()
    await waitFor(() =>
      expect(screen.getByLabelText("From date (UTC, inclusive)")).toHaveValue(
        "2026-10-18"
      )
    )
  })
  it("renders timestamp offsets as their actual UTC dates", () => {
    render(
      <AuditFilters
        search={{
          from: "2026-10-17T23:00:00-02:00",
          to: "2026-10-19T01:00:00+02:00",
        }}
        onChange={vi.fn()}
      />
    )
    expect(screen.getByLabelText("From date (UTC, inclusive)")).toHaveValue(
      "2026-10-18"
    )
    expect(screen.getByLabelText("Until date (UTC, exclusive)")).toHaveValue(
      "2026-10-18"
    )
  })
})
