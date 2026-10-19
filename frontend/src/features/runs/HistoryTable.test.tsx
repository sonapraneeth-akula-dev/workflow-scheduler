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
import { ApiError } from "@/lib/api/client"
import { getHistory, type HistoryRow, type HistorySearch } from "./history"
import { HistoryFilters, HistoryTable } from "./HistoryTable"

vi.mock("./history", async (original) => ({
  ...(await original<typeof import("./history")>()),
  getHistory: vi.fn(),
}))
vi.mock("@/features/series/RunNowButton", () => ({
  RunNowButton: ({
    scheduleId,
    seriesId,
  }: {
    scheduleId: string
    seriesId: string
  }) => (
    <button data-schedule={scheduleId} data-series={seriesId}>
      Run now
    </button>
  ),
}))
vi.mock("@tanstack/react-router", () => ({
  Link: ({
    to,
    params,
    children,
    title,
  }: {
    to: string
    params: Record<string, string>
    children: ReactNode
    title?: string
  }) => (
    <a
      title={title}
      href={to.replace(/\$([a-z]+)/g, (_, key: string) => params[key]!)}
    >
      {children}
    </a>
  ),
}))
const row: HistoryRow = {
  kind: "Run",
  at: "2026-10-16T07:00:00Z",
  workflowId: "team/release/build",
  seriesId: "019ed132-2f44-7000-8000-000000000003",
  runId: "019ed132-2f44-7000-8000-000000000010",
  versionId: "019ed132-2f44-7000-8000-000000000001",
  version: "1.2.3-rc.1+build.7",
  status: "Aborted",
  startedAt: "2026-10-16T07:00:00Z",
  endedAt: "2026-10-16T07:00:01Z",
  durationMs: 1000,
  trigger: "Manual",
  metadata: null,
  abort: {
    by: "019ed132-2f44-7000-8000-000000000004",
    byName: "Operator Name",
    at: "2026-10-16T07:00:00.1234567Z",
    reason: "Stop deployment",
    source: "UI",
  },
  variables: [
    { key: "credential", type: "Secret", redacted: true },
    { key: "count", type: "Long", redacted: false },
  ],
  fire: null,
}
const scope = { seriesId: row.seriesId! }
function show(search: HistorySearch = {}) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  return {
    ...render(
      <QueryClientProvider client={client}>
        <HistoryTable scope={scope} search={search} />
      </QueryClientProvider>
    ),
    client,
  }
}
function page(items: HistoryRow[] = [row], nextCursor: string | null = null) {
  return { items, nextCursor, currentScheduleId: null }
}
describe("historical table", () => {
  beforeEach(() => vi.clearAllMocks())
  it.each([
    ["2026-10-18T23:59:59.007999Z", "19 Oct 2026, 05:29:59.007 IST"],
    ["2026-10-19T05:29:59.007999+05:30", "19 Oct 2026, 05:29:59.007 IST"],
    ["2026-10-18T23:59:59Z", "19 Oct 2026, 05:29:59.000 IST"],
  ])(
    "shows %s in a separate IST date column with milliseconds",
    async (at, expected) => {
      vi.mocked(getHistory).mockResolvedValue(page([{ ...row, at }]))
      show()
      const table = await screen.findByRole("table")
      expect(
        within(table).getByRole("columnheader", { name: "Date / time (IST)" })
      ).toBeVisible()
      const date = within(within(table).getAllByRole("row")[1]!).getAllByRole(
        "cell"
      )[0]!
      expect(date).toHaveTextContent(expected)
      expect(date.querySelector("time")).toHaveAttribute("datetime", at)
      expect(date.querySelector("time")).toHaveAttribute("title", at)
      expect(within(date).queryByRole("link")).not.toBeInTheDocument()
      expect(date).not.toHaveTextContent(row.runId!)
    }
  )
  it("uses only the API's current schedule identity, including an empty filtered page", async () => {
    vi.mocked(getHistory).mockResolvedValue({
      ...page([]),
      currentScheduleId: row.versionId,
    })
    show({ status: "Succeeded" })
    expect(
      await screen.findByRole("button", { name: "Run now" })
    ).toHaveAttribute("data-schedule", row.versionId)
    expect(screen.getByRole("button", { name: "Run now" })).toHaveAttribute(
      "data-series",
      row.seriesId
    )
    expect(screen.getByText("No matching history.")).toBeVisible()
  })
  it("does not guess a schedule for a deleted or legacy series", async () => {
    vi.mocked(getHistory).mockResolvedValue(page([]))
    show()
    await screen.findByText(
      "Run now unavailable: this series has no current schedule."
    )
    expect(
      screen.queryByRole("button", { name: "Run now" })
    ).not.toBeInTheDocument()
  })
  it("renders exact committed abort and version links without variable columns or values", async () => {
    vi.mocked(getHistory).mockResolvedValue(page())
    show()
    const table = await screen.findByRole("table")
    expect(table).toHaveTextContent("Aborted")
    expect(table).toHaveTextContent("1000 ms")
    expect(table).toHaveTextContent("Manual")
    expect(table).toHaveTextContent("Operator Name")
    expect(table).toHaveTextContent(row.abort!.by)
    expect(table).toHaveTextContent("16 Oct 2026, 12:30:00.123 IST")
    expect(
      table.querySelector(`time[datetime="${row.abort!.at}"]`)
    ).toHaveAttribute("title", row.abort!.at)
    expect(table).toHaveTextContent("Stop deployment")
    expect(screen.getAllByRole("columnheader")).toHaveLength(7)
    expect(table).not.toHaveTextContent("Variables")
    expect(table).not.toHaveTextContent("credential")
    expect(table).not.toHaveTextContent("count (Long)")
    expect(screen.getByRole("link", { name: row.version! })).toHaveAttribute(
      "href",
      `/t/team/release/w/build/versions/${row.versionId}`
    )
    expect(screen.getByRole("link", { name: row.version! })).toHaveAttribute(
      "title",
      `Immutable version ID: ${row.versionId}`
    )
    expect(table).not.toHaveTextContent(row.versionId!)
    expect(screen.getByRole("link", { name: row.runId! })).toHaveAttribute(
      "href",
      `/runs/${row.runId}`
    )
  })
  it("keeps skips, unknown reasons and absent run facts explicit", async () => {
    vi.mocked(getHistory).mockResolvedValue(
      page([
        {
          ...row,
          kind: "Fire",
          runId: null,
          versionId: null,
          version: null,
          status: null,
          durationMs: null,
          trigger: null,
          abort: null,
          variables: [],
          startedAt: null,
          endedAt: null,
          fire: {
            scheduleId: row.versionId!,
            fireAt: row.at,
            outcome: "Skipped",
            reason: "overlap",
            scheduleConfigVersionId: null,
          },
        },
      ])
    )
    show()
    expect(await screen.findByRole("table")).toHaveTextContent(
      "Schedule skipped"
    )
    expect(screen.getByRole("table")).toHaveTextContent("overlap")
    expect(screen.getByRole("table")).toHaveTextContent("No run")
    expect(screen.getByRole("table")).toHaveTextContent("Not applicable")
    expect(screen.queryByText("0 ms")).not.toBeInTheDocument()
  })
  it("distinguishes schedule creation from the current run result without a missing-reason warning", async () => {
    vi.mocked(getHistory).mockResolvedValue(
      page([
        {
          ...row,
          status: "Succeeded",
          trigger: "Schedule",
          abort: null,
          fire: {
            scheduleId: row.versionId!,
            fireAt: row.at,
            outcome: "Started",
            reason: null,
            scheduleConfigVersionId: null,
          },
        },
      ])
    )
    show()
    const table = await screen.findByRole("table")
    expect(table).toHaveTextContent("Succeeded")
    expect(table).toHaveTextContent("Schedule triggered")
    expect(table).not.toHaveTextContent("Started")
    expect(table).not.toHaveTextContent("Reason not recorded")
    expect(table).not.toHaveTextContent("Scheduling reason not recorded")
  })
  it("keeps a missing reason explicit for a skipped schedule rather than inventing an explanation", async () => {
    vi.mocked(getHistory).mockResolvedValue(
      page([
        {
          ...row,
          kind: "Fire",
          runId: null,
          versionId: null,
          version: null,
          status: null,
          startedAt: null,
          endedAt: null,
          durationMs: null,
          trigger: null,
          abort: null,
          variables: [],
          fire: {
            scheduleId: row.versionId!,
            fireAt: row.at,
            outcome: "Skipped",
            reason: null,
            scheduleConfigVersionId: null,
          },
        },
      ])
    )
    show()
    expect(await screen.findByRole("table")).toHaveTextContent(
      "Scheduling reason not recorded."
    )
  })
  it("does not invent an actor for legacy Aborted rows", async () => {
    vi.mocked(getHistory).mockResolvedValue(page([{ ...row, abort: null }]))
    show()
    expect(await screen.findByRole("table")).toHaveTextContent("Aborted")
    expect(screen.getByRole("table")).toHaveTextContent(
      "Cancellation details not recorded"
    )
    expect(screen.queryByText("Operator Name")).not.toBeInTheDocument()
  })
  it("omits the cancellation column when no loaded run was cancelled", async () => {
    vi.mocked(getHistory).mockResolvedValue(
      page([{ ...row, status: "Succeeded", abort: null }])
    )
    show()
    await screen.findByRole("table")
    expect(
      screen.queryByRole("columnheader", { name: "Cancellation" })
    ).not.toBeInTheDocument()
    expect(screen.getAllByRole("columnheader")).toHaveLength(6)
    expect(screen.queryByText("Not cancelled")).not.toBeInTheDocument()
  })
  it("distinguishes unchanged successful runs from recorded and legacy cancellations", async () => {
    vi.mocked(getHistory).mockResolvedValue(
      page([
        row,
        {
          ...row,
          runId: "019ed132-2f44-7000-8000-000000000011",
          status: "Succeeded",
          abort: null,
        },
        {
          ...row,
          runId: "019ed132-2f44-7000-8000-000000000012",
          status: "Aborted",
          abort: null,
        },
      ])
    )
    show()
    await screen.findByRole("table")
    expect(
      screen.getByRole("columnheader", { name: "Cancellation" })
    ).toBeVisible()
    expect(screen.getByText("Not cancelled")).toBeVisible()
    expect(screen.getByText("Cancellation details not recorded")).toBeVisible()
    expect(screen.getByText("Operator Name")).toBeVisible()
  })
  it("announces loading, empty and failed reads without false success", async () => {
    vi.mocked(getHistory).mockResolvedValue(page([]))
    const { client } = show()
    expect(screen.getByText("Loading run history...")).toBeInTheDocument()
    await screen.findByText("No matching history.")
    vi.mocked(getHistory).mockRejectedValue(new Error("History offline."))
    await client.invalidateQueries()
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "History offline."
    )
  })
  it("loads more explicitly and preserves prior rows after an older-page error", async () => {
    vi.mocked(getHistory)
      .mockResolvedValueOnce(page([row], "next"))
      .mockRejectedValueOnce(new Error("Page failed."))
    show()
    fireEvent.click(
      await screen.findByRole("button", { name: "Load more history" })
    )
    expect(await screen.findByRole("alert")).toHaveTextContent("Page failed.")
    expect(screen.getByRole("table")).toHaveTextContent("Aborted")
    expect(getHistory).toHaveBeenLastCalledWith(
      scope,
      {},
      { cursor: "next", after: row },
      expect.any(AbortSignal)
    )
  })
  it.each([401, 403])(
    "hides cached rows after HTTP %i revocation",
    async (status) => {
      vi.mocked(getHistory).mockResolvedValue(page())
      show()
      await screen.findByRole("table")
      vi.mocked(getHistory).mockRejectedValue(
        new ApiError(new Response(null, { status }), {
          title: "Permission revoked.",
        })
      )
      fireEvent.click(
        screen.getByRole("button", { name: "Refresh run history" })
      )
      await screen.findByRole("alert")
      expect(screen.queryByRole("table")).not.toBeInTheDocument()
    }
  )
  it("uses keyboard-submitted exact abortedBy and typed sort/status filters", async () => {
    const user = userEvent.setup()
    const onChange = vi.fn()
    render(
      <HistoryFilters
        search={{
          status: "Aborted",
          abortedBy: row.abort!.by,
          sort: "createdAtAsc",
        }}
        onChange={onChange}
      />
    )
    expect(screen.getByLabelText("Run status")).toHaveValue("Aborted")
    expect(screen.getByLabelText("Cancelled by (principal UUID)")).toHaveValue(
      row.abort!.by
    )
    screen.getByRole("button", { name: "Filter history" }).focus()
    await user.keyboard("{Enter}")
    expect(onChange).toHaveBeenCalledWith({
      status: "Aborted",
      abortedBy: row.abort!.by,
      sort: "createdAtAsc",
    })
    await user.clear(screen.getByLabelText("Cancelled by (principal UUID)"))
    await user.type(
      screen.getByLabelText("Cancelled by (principal UUID)"),
      "not-a-principal"
    )
    await user.click(screen.getByRole("button", { name: "Filter history" }))
    await waitFor(() =>
      expect(screen.getByRole("alert")).toHaveTextContent("principal UUID")
    )
    expect(onChange).toHaveBeenCalledTimes(1)
    await user.click(
      screen.getByRole("button", { name: "Clear history filters" })
    )
    expect(onChange).toHaveBeenLastCalledWith({})
  })
})
