import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import type { LiveSnapshot } from "@/lib/live/subscriptions"
import { useLive } from "@/lib/live/useLive"
import { getReportIndex, type ReportItem } from "./api"
import { groupReports, ReportIndex } from "./ReportIndex"

vi.mock("./api", () => ({ getReportIndex: vi.fn() }))
vi.mock("@/lib/live/useLive", () => ({ useLive: vi.fn() }))
vi.mock("./ReportView", () => ({
  ReportView: ({
    runId,
    reportId,
    embedded,
  }: {
    runId: string
    reportId: string
    embedded: boolean
  }) => (
    <section aria-label="Report viewer">
      {runId}/{reportId} {embedded ? "embedded" : "standalone"}
    </section>
  ),
}))
const runId = "019ed132-2f44-7000-8000-000000000001"
const base: ReportItem = {
  signOff: { revision: "0", status: "NotSignedOff", latest: null },
  id: "019ed132-2f44-7000-8000-000000000010",
  runId,
  sourceKey: "$post/summary",
  attempt: 2,
  kind: "markdown",
  title: "Release summary",
  tags: ["review"],
  sizeBytes: 1024,
  createdAt: "2026-10-16T08:00:00+00:00",
}
const reports: ReportItem[] = [
  base,
  {
    ...base,
    id: "019ed132-2f44-7000-8000-000000000011",
    sourceKey: "build/measure",
    kind: "json",
    title: "Metrics",
    tags: ["review", "numeric"],
  },
  {
    ...base,
    id: "019ed132-2f44-7000-8000-000000000012",
    sourceKey: "build/verify",
    kind: "json",
    title: "Other metrics",
    tags: ["other"],
  },
]
let live: LiveSnapshot
const retry = vi.fn()
function show() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  const content = () => (
    <QueryClientProvider client={client}>
      <ReportIndex runId={runId} />
    </QueryClientProvider>
  )
  const view = render(content())
  return { ...view, redraw: () => view.rerender(content()) }
}
beforeEach(() => {
  vi.resetAllMocks()
  live = {
    resource: { scope: "run", id: runId },
    status: "live",
    error: null,
    delta: null,
    resyncVersion: 0,
    reportVersion: 0,
  }
  vi.mocked(useLive).mockImplementation(() => ({ snapshot: live, retry }))
  vi.mocked(getReportIndex).mockResolvedValue({ runId, items: reports })
})
describe("grouped live report index", () => {
  it("opens each report in an embedded subtab with retained source and attempt", async () => {
    show()
    const summary = await screen.findByRole("tab", {
      name: "Release summary $post/summary | Attempt 2 Not signed off",
    })
    expect(summary).toHaveAttribute("aria-selected", "true")
    expect(
      screen.getByRole("region", { name: "Report viewer" })
    ).toHaveTextContent(`${runId}/${base.id} embedded`)
    await userEvent.click(
      screen.getByRole("tab", {
        name: "Metrics build/measure | Attempt 2 Not signed off",
      })
    )
    expect(
      screen.getByRole("region", { name: "Report viewer" })
    ).toHaveTextContent(`${runId}/${reports[1]!.id} embedded`)
    expect(screen.getAllByRole("tabpanel")).toHaveLength(1)
    expect(screen.queryByRole("link")).not.toBeInTheDocument()
    expect(useLive).toHaveBeenCalledWith({ scope: "run", id: runId })
    expect(screen.getByText("3 of 3 reports")).toBeVisible()
  })
  it("combines exact kind/tag filters with AND and clears without another HTTP query", async () => {
    show()
    await screen.findByRole("tab", { name: /^Metrics / })
    const before = vi.mocked(getReportIndex).mock.calls.length
    fireEvent.change(screen.getByRole("combobox", { name: "Report kind" }), {
      target: { value: "json" },
    })
    expect(screen.getAllByRole("tab")).toHaveLength(2)
    fireEvent.change(screen.getByRole("combobox", { name: "Report tag" }), {
      target: { value: "review" },
    })
    expect(screen.getAllByRole("tab")).toHaveLength(1)
    expect(screen.getByRole("tab")).toHaveTextContent("Metrics")
    expect(screen.getByText("1 of 3 reports")).toBeVisible()
    expect(screen.getByRole("option", { name: "markdown" })).toBeInTheDocument()
    fireEvent.click(
      screen.getByRole("button", { name: "Clear report filters" })
    )
    expect(screen.getAllByRole("tab")).toHaveLength(3)
    expect(getReportIndex).toHaveBeenCalledTimes(before)
  })
  it("inserts committed data after ReportAdded without reload or a fake lifecycle event", async () => {
    const view = show()
    await screen.findByRole("tab", { name: /^Metrics / })
    await userEvent.click(screen.getByRole("tab", { name: /^Metrics / }))
    const added = {
      ...base,
      id: "019ed132-2f44-7000-8000-000000000013",
      title: "Live result",
    }
    vi.mocked(getReportIndex).mockResolvedValue({
      runId,
      items: [...reports, added],
    })
    live = { ...live, reportVersion: 1 }
    view.redraw()
    expect(
      await screen.findByRole("tab", { name: /^Live result / })
    ).toBeVisible()
    expect(screen.getByRole("tab", { name: /^Metrics / })).toHaveAttribute(
      "aria-selected",
      "true"
    )
    expect(screen.getByText("4 of 4 reports")).toBeVisible()
    expect(live.delta).toBeNull()
  })
  it("refetches missed durable rows after a reconnect/resync", async () => {
    const view = show()
    await screen.findByRole("tab", { name: /^Metrics / })
    live = { ...live, status: "reconnecting" }
    view.redraw()
    expect(screen.getByText(/Live report updates reconnecting/)).toBeVisible()
    vi.mocked(getReportIndex).mockResolvedValue({ runId, items: [base] })
    live = { ...live, status: "live", resyncVersion: 1 }
    view.redraw()
    await waitFor(() => expect(screen.getAllByRole("tab")).toHaveLength(1))
    expect(screen.getByText("1 of 1 reports")).toBeVisible()
  })
  it("cancels an in-flight stale snapshot when a report commits and ignores its late response", async () => {
    const view = show()
    await screen.findByRole("tab", { name: /^Metrics / })
    let complete:
      ((value: { runId: string; items: ReportItem[] }) => void) | undefined
    const pending = new Promise<{ runId: string; items: ReportItem[] }>(
      (resolve) => {
        complete = resolve
      }
    )
    const added = {
      ...base,
      id: "019ed132-2f44-7000-8000-000000000013",
      title: "Racing result",
    }
    vi.mocked(getReportIndex)
      .mockReturnValueOnce(pending)
      .mockResolvedValue({ runId, items: [...reports, added] })
    fireEvent.click(screen.getByRole("button", { name: "Refresh reports" }))
    live = { ...live, reportVersion: 1 }
    view.redraw()
    expect(
      await screen.findByRole("tab", { name: /^Racing result / })
    ).toBeVisible()
    await act(async () => complete?.({ runId, items: reports }))
    expect(screen.getByRole("tab", { name: /^Racing result / })).toBeVisible()
  })
  it.each([401, 403, 503])(
    "hides stale report rows after refused refresh HTTP %i",
    async (status) => {
      show()
      await screen.findByRole("tab", { name: /^Metrics / })
      vi.mocked(getReportIndex).mockRejectedValue(
        new ApiError(new Response(null, { status }), {
          title: "Reports unavailable.",
        })
      )
      fireEvent.click(screen.getByRole("button", { name: "Refresh reports" }))
      expect(await screen.findByRole("alert")).toHaveTextContent(
        "Reports unavailable."
      )
      expect(screen.queryByRole("tab")).not.toBeInTheDocument()
      expect(
        screen.queryByRole("region", { name: "Report viewer" })
      ).not.toBeInTheDocument()
      expect(
        screen.queryByText("No reports published for this run.")
      ).not.toBeInTheDocument()
    }
  )
  it("fails closed on live scope errors and exposes explicit connection retry", async () => {
    const view = show()
    await screen.findByRole("tab", { name: /^Metrics / })
    live = {
      ...live,
      status: "error",
      error: new Error("Report subscription denied."),
    }
    view.redraw()
    expect(screen.getByRole("alert")).toHaveTextContent(
      "Report subscription denied."
    )
    expect(screen.queryByRole("tab")).not.toBeInTheDocument()
    expect(
      screen.queryByRole("region", { name: "Report viewer" })
    ).not.toBeInTheDocument()
    fireEvent.click(
      screen.getByRole("button", { name: "Retry report updates" })
    )
    expect(retry).toHaveBeenCalledOnce()
  })
  it("distinguishes a genuinely empty committed run index", async () => {
    vi.mocked(getReportIndex).mockResolvedValue({ runId, items: [] })
    show()
    expect(
      await screen.findByText("No reports published for this run.")
    ).toBeVisible()
    expect(screen.getByText("0 of 0 reports")).toBeVisible()
    expect(screen.queryByRole("tab")).not.toBeInTheDocument()
  })
  it("supports manual keyboard activation without loading reports merely on focus", async () => {
    const user = userEvent.setup()
    show()
    const first = await screen.findByRole("tab", { name: /^Release summary / })
    first.focus()
    await user.keyboard("{ArrowRight}")
    const next = screen.getByRole("tab", { name: /^Metrics / })
    expect(next).toHaveFocus()
    expect(first).toHaveAttribute("aria-selected", "true")
    await user.keyboard("{Enter}")
    expect(next).toHaveAttribute("aria-selected", "true")
    expect(
      screen.getByRole("region", { name: "Report viewer" })
    ).toHaveTextContent(reports[1]!.id)
  })
  it("selects a visible report when filters hide the current selection and restores it when cleared", async () => {
    show()
    await userEvent.click(
      await screen.findByRole("tab", { name: /^Other metrics / })
    )
    fireEvent.change(screen.getByRole("combobox", { name: "Report kind" }), {
      target: { value: "markdown" },
    })
    expect(screen.getByRole("tab")).toHaveAttribute("aria-selected", "true")
    expect(
      screen.getByRole("region", { name: "Report viewer" })
    ).toHaveTextContent(base.id)
    fireEvent.click(
      screen.getByRole("button", { name: "Clear report filters" })
    )
    expect(
      screen.getByRole("tab", { name: /^Other metrics / })
    ).toHaveAttribute("aria-selected", "true")
  })
  it("distinguishes duplicate titles by exact source and attempt", async () => {
    vi.mocked(getReportIndex).mockResolvedValue({
      runId,
      items: [
        base,
        { ...base, id: reports[1]!.id, sourceKey: "build/measure", attempt: 3 },
      ],
    })
    show()
    expect(
      await screen.findByRole("tab", {
        name: "Release summary $post/summary | Attempt 2 Not signed off",
      })
    ).toBeVisible()
    await userEvent.click(
      screen.getByRole("tab", {
        name: "Release summary build/measure | Attempt 3 Not signed off",
      })
    )
    expect(
      screen.getByRole("region", { name: "Report viewer" })
    ).toHaveTextContent(reports[1]!.id)
  })
  it("groups special own keys without prototype lookups and rejects malformed sources", () => {
    const groups = groupReports([
      { ...base, sourceKey: "__proto__/constructor" },
    ])
    expect(groups[0]?.source).toBe("__proto__")
    expect(groups[0]?.tasks.get("constructor")).toEqual([
      { ...base, sourceKey: "__proto__/constructor" },
    ])
    expect(() =>
      groupReports([{ ...base, sourceKey: "node/task/extra" }])
    ).toThrow("exact node/task")
  })
})
