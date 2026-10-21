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
import { SignOffPanel } from "./SignOffPanel"
import { changeSignOff, getSignOff, type SignOffResponse } from "./signoff-api"

const access = vi.hoisted(() => ({
  viewer: true,
  operator: true,
  checking: false,
}))
vi.mock("@/lib/auth/usePermissions", () => ({
  usePermissions: () => ({
    isSuccess: true,
    data: { viewer: access.viewer },
    can: (role: string) =>
      !access.checking &&
      (role === "Operator" ? access.operator : access.viewer),
    error: null,
  }),
}))
vi.mock("./signoff-api", async (original) => ({
  ...(await original<typeof import("./signoff-api")>()),
  changeSignOff: vi.fn(),
  getSignOff: vi.fn(),
}))
const runId = "019ed132-2f44-7000-8000-000000000001"
const reportId = "019ed132-2f44-7000-8000-000000000002"
const initial: SignOffResponse = {
  runId,
  reportId: null,
  workflowId: "team/type/workflow",
  versionId: "11111111-1111-4111-8111-111111111111",
  isTerminal: true,
  state: { revision: "0", status: "NotSignedOff", latest: null },
  history: [],
  nextBefore: null,
}
const event = {
  revision: "1",
  action: "SignOff" as const,
  reason: "Checked <script>alert(1)</script>",
  actorId: "11111111-1111-4111-8111-111111111111",
  actorName: "Review owner",
  at: "2026-10-21T00:00:00Z",
}
const signed: SignOffResponse = {
  ...initial,
  state: { revision: "1", status: "SignedOff", latest: event },
  history: [event],
}
function show(report = false) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  const tree = () => (
    <QueryClientProvider client={client}>
      <SignOffPanel runId={runId} {...(report ? { reportId } : {})} />
    </QueryClientProvider>
  )
  const result = render(tree())
  return { client, rerender: () => result.rerender(tree()) }
}

describe("run-scoped sign-off controls and history", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    access.viewer = true
    access.operator = true
    access.checking = false
    vi.mocked(getSignOff).mockResolvedValue(initial)
  })
  it("requires a reason, confirms pinned scope and records a run approval", async () => {
    vi.mocked(changeSignOff).mockImplementation(async () => {
      vi.mocked(getSignOff).mockResolvedValue(signed)
      return signed
    })
    show()
    await userEvent.click(
      await screen.findByRole("button", { name: "Sign off workflow" })
    )
    const dialog = screen.getByRole("alertdialog")
    expect(dialog).toHaveTextContent("not its reports or future runs")
    expect(
      within(dialog).getByRole("button", { name: "Confirm sign-off" })
    ).toBeDisabled()
    fireEvent.change(screen.getByLabelText("Sign-off reason"), {
      target: { value: "   " },
    })
    expect(
      within(dialog).getByRole("button", { name: "Confirm sign-off" })
    ).toBeDisabled()
    fireEvent.change(screen.getByLabelText("Sign-off reason"), {
      target: { value: "Reviewed all results" },
    })
    await userEvent.click(
      within(dialog).getByRole("button", { name: "Confirm sign-off" })
    )
    await waitFor(() =>
      expect(changeSignOff).toHaveBeenCalledWith(
        { runId },
        "0",
        "SignOff",
        "Reviewed all results"
      )
    )
    await waitFor(() =>
      expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument()
    )
    expect(await screen.findByText("Signed off", { exact: true })).toBeVisible()
    expect(screen.getAllByText("Review owner")[0]).toBeVisible()
    expect(document.querySelector("script")).toBeNull()
  })
  it("lets a Viewer inspect permanent history but never change approval", async () => {
    access.operator = false
    vi.mocked(getSignOff).mockResolvedValue(signed)
    show()
    await screen.findAllByText("Review owner")
    expect(
      screen.queryByRole("button", { name: "Revoke sign-off" })
    ).not.toBeInTheDocument()
    fireEvent.click(screen.getByText("Sign-off history", { exact: true }))
    expect(screen.getAllByText(event.reason)).toHaveLength(2)
    expect(document.querySelector("script")).toBeNull()
    expect(changeSignOff).not.toHaveBeenCalled()
  })
  it("offers independent report revocation with a reason and the exact current revision", async () => {
    vi.mocked(getSignOff).mockResolvedValue({ ...signed, reportId })
    const revoked = {
      ...event,
      revision: "2",
      action: "Revoke" as const,
      reason: "Regression found",
    }
    const updated: SignOffResponse = {
      ...signed,
      reportId,
      state: { revision: "2", status: "Revoked", latest: revoked },
      history: [revoked, event],
    }
    vi.mocked(changeSignOff).mockImplementation(async () => {
      vi.mocked(getSignOff).mockResolvedValue(updated)
      return updated
    })
    show(true)
    await userEvent.click(
      await screen.findByRole("button", { name: "Revoke sign-off" })
    )
    expect(screen.getByRole("alertdialog")).toHaveTextContent(
      "only to this report"
    )
    fireEvent.change(screen.getByLabelText("Revocation reason"), {
      target: { value: "Regression found" },
    })
    await userEvent.click(
      screen.getByRole("button", { name: "Confirm revocation" })
    )
    await waitFor(() =>
      expect(changeSignOff).toHaveBeenCalledWith(
        { runId, reportId },
        "1",
        "Revoke",
        "Regression found"
      )
    )
    expect(await screen.findByText("Revoked", { exact: true })).toBeVisible()
  })
  it("preserves uncertain changes without an automatic or edited retry", async () => {
    vi.mocked(changeSignOff).mockRejectedValue(new Error("Connection lost"))
    show()
    await userEvent.click(
      await screen.findByRole("button", { name: "Sign off workflow" })
    )
    fireEvent.change(screen.getByLabelText("Sign-off reason"), {
      target: { value: "Reviewed" },
    })
    await userEvent.click(
      screen.getByRole("button", { name: "Confirm sign-off" })
    )
    expect(await screen.findByText("Connection lost")).toBeVisible()
    expect(screen.getByLabelText("Sign-off reason")).toHaveValue("Reviewed")
    expect(screen.getByLabelText("Sign-off reason")).toHaveAttribute("readonly")
    expect(
      screen.getByRole("button", { name: "Confirm sign-off" })
    ).toBeDisabled()
    expect(changeSignOff).toHaveBeenCalledOnce()
  })
  it("hides stale state on current permission loss and disables a draft during permission checks", async () => {
    const shown = show()
    await userEvent.click(
      await screen.findByRole("button", { name: "Sign off workflow" })
    )
    fireEvent.change(screen.getByLabelText("Sign-off reason"), {
      target: { value: "Draft" },
    })
    access.checking = true
    shown.rerender()
    expect(
      screen.getByRole("button", { name: "Confirm sign-off" })
    ).toBeDisabled()
    expect(screen.getByLabelText("Sign-off reason")).toHaveValue("Draft")
    access.viewer = false
    access.operator = false
    shown.rerender()
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument()
    expect(screen.queryByText("Not signed off")).not.toBeInTheDocument()
    expect(screen.getByRole("alert")).toHaveTextContent("Viewer permission")
  })
  it("does not offer approval before terminal completion and surfaces failed refreshes", async () => {
    vi.mocked(getSignOff).mockResolvedValue({ ...initial, isTerminal: false })
    const shown = show()
    await screen.findByText(/Sign-off becomes available/)
    expect(
      screen.queryByRole("button", { name: "Sign off workflow" })
    ).not.toBeInTheDocument()
    vi.mocked(getSignOff).mockRejectedValue(new Error("Sign-off unavailable"))
    await shown.client.invalidateQueries({ queryKey: ["signoff"] })
    expect(await screen.findByText("Sign-off unavailable")).toBeVisible()
    expect(screen.queryByText("Not signed off")).not.toBeInTheDocument()
  })
  it("loads older history without replacing the current approval", async () => {
    vi.mocked(getSignOff).mockImplementation(async (_target, before) =>
      before
        ? { ...signed, history: [], nextBefore: null }
        : { ...signed, nextBefore: "1" }
    )
    show()
    await screen.findAllByText("Review owner")
    fireEvent.click(screen.getByText("Sign-off history", { exact: true }))
    await userEvent.click(
      screen.getByRole("button", { name: "Load older sign-offs" })
    )
    await waitFor(() =>
      expect(getSignOff).toHaveBeenCalledWith(
        { runId },
        "1",
        expect.any(AbortSignal)
      )
    )
    expect(screen.getByText("Signed off", { exact: true })).toBeVisible()
  })
})
