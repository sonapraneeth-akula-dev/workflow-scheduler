import type { ReactNode } from "react"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import { getHistory, type HistoryRow } from "@/features/runs/history"
import { getReportComparison, getReportIndex, type ReportItem } from "./api"
import { ReportCompare } from "./ReportCompare"

vi.mock("./api", () => ({
  getReportComparison: vi.fn(),
  getReportIndex: vi.fn(),
}))
vi.mock("@/features/runs/history", () => ({ getHistory: vi.fn() }))
vi.mock("@tanstack/react-router", () => ({
  Link: ({ children }: { children: ReactNode }) => <a href="/">{children}</a>,
}))
vi.mock("@/renderers/ReportRenderer", () => ({
  ReportRenderer: ({
    title,
    kind,
    content,
  }: {
    title: string
    kind: string
    content: Blob
  }) => (
    <p>
      Renderer {kind}: {title} ({content.size} bytes)
    </p>
  ),
}))
const seriesId = "019ed132-2f44-7000-8000-000000000003"
const runIds = [
  "019ed132-2f44-7000-8000-000000000010",
  "019ed132-2f44-7000-8000-000000000011",
  "019ed132-2f44-7000-8000-000000000012",
]
const row = (runId: string): HistoryRow => ({
  kind: "Run",
  runId,
  seriesId,
  workflowId: "team/build/basic",
  at: "2026-10-16T07:00:00Z",
  versionId: seriesId,
  version: "1.0.0",
  status: "Succeeded",
  startedAt: null,
  endedAt: null,
  durationMs: null,
  trigger: "Manual",
  metadata: null,
  abort: null,
  variables: [],
  fire: null,
})
const report: ReportItem = {
  id: "019ed132-2f44-7000-8000-000000000020",
  runId: runIds[0]!,
  sourceKey: "$post/summary",
  title: " Exact \u03bb title ",
  kind: "text",
  attempt: 1,
  sizeBytes: 7,
  tags: [],
  createdAt: "2026-10-16T08:00:00Z",
}
const comparison = {
  left: { report, content: new Blob(["payload"]) },
  right: {
    report: {
      ...report,
      id: "019ed132-2f44-7000-8000-000000000021",
      runId: runIds[1]!,
      kind: "markdown",
      attempt: 2,
    },
    content: new Blob(["payload"]),
  },
}
function show() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  render(
    <QueryClientProvider client={client}>
      <ReportCompare seriesId={seriesId} />
    </QueryClientProvider>
  )
  return client
}
async function selectPair(left = runIds[0]!, right = runIds[1]!) {
  await screen.findAllByRole("option", { name: new RegExp(runIds[0]!) })
  fireEvent.change(screen.getByRole("combobox", { name: "Left run" }), {
    target: { value: left },
  })
  fireEvent.change(screen.getByRole("combobox", { name: "Right run" }), {
    target: { value: right },
  })
  await screen.findByRole("option", { name: /Exact .* title/ })
  fireEvent.change(
    screen.getByRole("combobox", { name: "Report from left run" }),
    { target: { value: report.id } }
  )
  fireEvent.click(
    screen.getByRole("button", { name: "Compare selected reports" })
  )
}
describe("series report comparison", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    vi.mocked(getHistory).mockResolvedValue({
      items: runIds.map(row),
      nextCursor: null,
      currentScheduleId: null,
    })
    vi.mocked(getReportIndex).mockResolvedValue({
      runId: report.runId,
      items: [report],
    })
    vi.mocked(getReportComparison).mockResolvedValue(comparison)
  })
  it("selects any pair and reuses each side's own kind renderer and committed metadata", async () => {
    show()
    await selectPair()
    const left = await screen.findByRole("region", { name: "Left report" })
    const right = screen.getByRole("region", { name: "Right report" })
    expect(within(left).getByText(/Renderer text:/)).toHaveTextContent(
      "Renderer text: Exact \u03bb title (7 bytes)"
    )
    expect(within(right).getByText(/Renderer markdown:/)).toBeVisible()
    expect(within(right).getByText(/Attempt 2/)).toBeVisible()
    expect(getReportComparison).toHaveBeenCalledWith(
      seriesId,
      {
        leftRunId: runIds[0],
        rightRunId: runIds[1],
        sourceKey: report.sourceKey,
        title: report.title,
      },
      expect.any(AbortSignal)
    )
    expect(getHistory).toHaveBeenCalledWith(
      { seriesId },
      { outcome: "Run" },
      null,
      expect.any(AbortSignal)
    )
  })
  it("pages beyond the initial history to select an older run", async () => {
    vi.mocked(getHistory)
      .mockResolvedValueOnce({
        items: runIds.slice(0, 2).map(row),
        nextCursor: "older",
        currentScheduleId: null,
      })
      .mockResolvedValueOnce({
        items: [row(runIds[2]!)],
        nextCursor: null,
        currentScheduleId: null,
      })
    show()
    fireEvent.click(
      await screen.findByRole("button", { name: "Load more series runs" })
    )
    await waitFor(() =>
      expect(
        screen.getAllByRole("option", { name: new RegExp(runIds[2]!) })
      ).toHaveLength(2)
    )
    await selectPair(runIds[0], runIds[2])
    await screen.findByRole("region", { name: "Left report" })
    expect(getHistory).toHaveBeenLastCalledWith(
      { seriesId },
      { outcome: "Run" },
      { cursor: "older", after: row(runIds[1]!) },
      expect.any(AbortSignal)
    )
    expect(getReportComparison).toHaveBeenCalledWith(
      seriesId,
      expect.objectContaining({ rightRunId: runIds[2] }),
      expect.any(AbortSignal)
    )
  })
  it("requires a committed report and distinct runs before comparing", async () => {
    show()
    await screen.findAllByRole("option", { name: new RegExp(runIds[0]!) })
    expect(
      screen.getByRole("button", { name: "Compare selected reports" })
    ).toBeDisabled()
    fireEvent.change(screen.getByRole("combobox", { name: "Left run" }), {
      target: { value: runIds[0] },
    })
    expect(
      within(screen.getByRole("combobox", { name: "Right run" })).getByRole(
        "option",
        { name: new RegExp(runIds[0]!) }
      )
    ).toBeDisabled()
    expect(getReportComparison).not.toHaveBeenCalled()
  })
  it("clears a previous comparison immediately after either run changes", async () => {
    show()
    await selectPair()
    await screen.findByRole("region", { name: "Left report" })
    fireEvent.change(screen.getByRole("combobox", { name: "Right run" }), {
      target: { value: runIds[2] },
    })
    expect(screen.queryByRole("region", { name: "Left report" })).toBeNull()
    fireEvent.click(
      screen.getByRole("button", { name: "Compare selected reports" })
    )
    await screen.findByRole("region", { name: "Left report" })
    fireEvent.change(screen.getByRole("combobox", { name: "Left run" }), {
      target: { value: runIds[1] },
    })
    expect(screen.queryByRole("region", { name: "Left report" })).toBeNull()
    expect(
      screen.getByRole("combobox", { name: "Report from left run" })
    ).toHaveValue("")
  })
  it.each([403, 404, 409, 503])(
    "hides both stale sides after comparison refresh fails HTTP %i",
    async (status) => {
      show()
      await selectPair()
      await screen.findByRole("region", { name: "Left report" })
      vi.mocked(getReportComparison).mockRejectedValue(
        new ApiError(new Response(null, { status }), {
          title:
            status === 409 ? "Multiple exact matches." : "Match unavailable.",
        })
      )
      fireEvent.click(
        screen.getByRole("button", { name: "Refresh comparison" })
      )
      expect(await screen.findByRole("alert")).toHaveTextContent(
        status === 409 ? "Multiple exact matches." : "Match unavailable."
      )
      expect(screen.queryByRole("region", { name: "Left report" })).toBeNull()
      expect(screen.queryByRole("region", { name: "Right report" })).toBeNull()
      expect(
        screen.getByRole("button", { name: "Refresh comparison" })
      ).toBeEnabled()
    }
  )
  it("hides picks and comparison after history access is revoked", async () => {
    show()
    await selectPair()
    await screen.findByRole("region", { name: "Left report" })
    vi.mocked(getHistory).mockRejectedValue(new Error("Series access revoked."))
    fireEvent.click(screen.getByRole("button", { name: "Refresh series runs" }))
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Series access revoked."
    )
    expect(screen.queryByRole("region", { name: "Left report" })).toBeNull()
    expect(screen.getByRole("combobox", { name: "Left run" })).toBeDisabled()
    expect(
      screen.getByRole("combobox", { name: "Report from left run" })
    ).toBeDisabled()
  })
  it("surfaces index failure instead of presenting a fabricated empty match", async () => {
    vi.mocked(getReportIndex).mockRejectedValue(new Error("Reports offline."))
    show()
    await screen.findAllByRole("option", { name: new RegExp(runIds[0]!) })
    fireEvent.change(screen.getByRole("combobox", { name: "Left run" }), {
      target: { value: runIds[0] },
    })
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Reports offline."
    )
    expect(getReportComparison).not.toHaveBeenCalled()
    vi.mocked(getReportIndex).mockResolvedValue({
      runId: report.runId,
      items: [report],
    })
    fireEvent.click(
      screen.getByRole("button", { name: "Refresh left run reports" })
    )
    await screen.findByRole("option", { name: /Exact .* title/ })
    expect(screen.queryByRole("alert")).toBeNull()
    expect(
      screen.getByRole("combobox", { name: "Report from left run" })
    ).toHaveValue("")
  })
  it("shows empty history and a left run with no reports explicitly", async () => {
    vi.mocked(getHistory).mockResolvedValueOnce({
      items: [],
      nextCursor: null,
      currentScheduleId: null,
    })
    vi.mocked(getReportIndex).mockResolvedValue({
      runId: report.runId,
      items: [],
    })
    show()
    await screen.findByText("No runs in this series.")
    fireEvent.click(screen.getByRole("button", { name: "Refresh series runs" }))
    await screen.findAllByRole("option", { name: new RegExp(runIds[0]!) })
    fireEvent.change(screen.getByRole("combobox", { name: "Left run" }), {
      target: { value: runIds[0] },
    })
    await screen.findByText("No reports published for the left run.")
    expect(getReportComparison).not.toHaveBeenCalled()
  })
  it("does not show either side while comparison content is loading", async () => {
    vi.mocked(getReportComparison).mockImplementation(
      () => new Promise(() => {})
    )
    show()
    await selectPair()
    expect(await screen.findByRole("status")).toHaveTextContent(
      "Loading comparison..."
    )
    expect(screen.queryByRole("region", { name: "Left report" })).toBeNull()
    expect(
      screen.getByRole("button", { name: "Refresh comparison" })
    ).toBeDisabled()
  })
})
