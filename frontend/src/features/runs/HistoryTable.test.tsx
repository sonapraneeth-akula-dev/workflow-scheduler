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
import { formatTimestamp } from "@/lib/timestamps"
import {
  getHistory,
  type HistoryRow,
  type HistoryScope,
  type HistorySearch,
} from "./history"
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
function show(search: HistorySearch = {}, historyScope: HistoryScope = scope) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  return {
    ...render(
      <QueryClientProvider client={client}>
        <HistoryTable scope={historyScope} search={search} />
      </QueryClientProvider>
    ),
    client,
  }
}
const schedules = [
  {
    seriesId: row.seriesId!,
    scheduleId: "019ed132-2f44-7000-8000-000000000020",
    name: "Training & validation",
    paused: false,
    deleted: false,
  },
  {
    seriesId: "019ed132-2f44-7000-8000-000000000005",
    scheduleId: "019ed132-2f44-7000-8000-000000000021",
    name: "Full test",
    paused: true,
    deleted: false,
  },
]
function page(items: HistoryRow[] = [row], nextCursor: string | null = null) {
  return {
    items,
    nextCursor,
    currentScheduleId: null,
    schedules: [] as typeof schedules,
  }
}
describe("historical table", () => {
  beforeEach(() => vi.clearAllMocks())
  it("shows the recorded triggering user's name with Manual in the trigger cell", async () => {
    vi.mocked(getHistory).mockResolvedValue(
      page([{ ...row, triggeredByName: "Original triggering operator" }])
    )
    show()
    const table = await screen.findByRole("table")
    const cell = within(table).getByRole("cell", { name: /Manual/ })
    expect(cell).toHaveTextContent("By Original triggering operator")
    expect(cell).not.toHaveTextContent(row.abort!.byName)
  })
  it.each([null, undefined])(
    "keeps missing legacy actor names explicit instead of inventing a user: %s",
    async (triggeredByName) => {
      vi.mocked(getHistory).mockResolvedValue(
        page([{ ...row, triggeredByName }])
      )
      show()
      const table = await screen.findByRole("table")
      expect(
        within(table).getByRole("cell", { name: /Manual/ })
      ).toHaveTextContent("User name not recorded")
    }
  )
  it.each(["Schedule", "CatchUp", "Rerun"] as const)(
    "does not attach manual-user text to %s triggers",
    async (trigger) => {
      vi.mocked(getHistory).mockResolvedValue(
        page([{ ...row, trigger, triggeredByName: "Not a manual actor" }])
      )
      show()
      await screen.findByRole("table")
      expect(
        screen.queryByText("By Not a manual actor")
      ).not.toBeInTheDocument()
      expect(
        screen.queryByText("User name not recorded")
      ).not.toBeInTheDocument()
    }
  )
  it("renders recorded names as literal text, not markup", async () => {
    const name = "<img src=x onerror=alert()>"
    vi.mocked(getHistory).mockResolvedValue(
      page([{ ...row, triggeredByName: name }])
    )
    show()
    const table = await screen.findByRole("table")
    expect(within(table).getByText(`By ${name}`)).toBeVisible()
    expect(table.querySelector("img")).toBeNull()
  })
  it("names multiple tracked schedules in the same history table without separate series links", async () => {
    const otherSeriesId = "019ed132-2f44-7000-8000-000000000005"
    vi.mocked(getHistory).mockResolvedValue({
      ...page([
        row,
        { ...row, runId: "019ed132-2f44-7000-8000-000000000011" },
        {
          ...row,
          runId: "019ed132-2f44-7000-8000-000000000012",
          seriesId: otherSeriesId,
        },
        {
          ...row,
          runId: "019ed132-2f44-7000-8000-000000000013",
          seriesId: null,
        },
      ]),
      schedules,
    })
    show({}, { workflowId: row.workflowId })
    const table = await screen.findByRole("table")
    expect(
      within(table).getByRole("columnheader", { name: "Schedule" })
    ).toBeVisible()
    expect(screen.getByLabelText("Tracked history sources")).toHaveTextContent(
      "Training & Validation, Full Test (paused)"
    )
    const dataRows = within(table).getAllByRole("row").slice(1)
    expect(
      dataRows.map((item) =>
        within(item)
          .getAllByRole("cell")
          .some((cell) => cell.textContent === "Training & Validation")
      )
    ).toEqual([true, true, false, false])
    expect(within(dataRows[2]!).getByText("Full Test (paused)")).toBeVisible()
    expect(within(dataRows[3]!).getByText("Unscheduled run")).toBeVisible()
    expect(
      screen.queryByRole("navigation", { name: "Schedule series histories" })
    ).not.toBeInTheDocument()
    expect(
      within(table).queryByRole("link", { name: /series history/i })
    ).not.toBeInTheDocument()
    expect(
      within(table)
        .getAllByRole("rowheader")
        .map((cell) => cell.textContent)
    ).toEqual([
      row.runId,
      "019ed132-2f44-7000-8000-000000000011",
      "019ed132-2f44-7000-8000-000000000012",
      "019ed132-2f44-7000-8000-000000000013",
    ])
  })
  it("shows one tracked schedule by name without an unnecessary schedule column", async () => {
    vi.mocked(getHistory).mockResolvedValue({
      ...page(),
      schedules: [schedules[0]!],
    })
    show({}, { workflowId: row.workflowId })
    await screen.findByRole("table")
    expect(screen.getByLabelText("Tracked history sources")).toHaveTextContent(
      "Training & Validation"
    )
    expect(
      screen.queryByRole("columnheader", { name: "Schedule" })
    ).not.toBeInTheDocument()
    expect(
      screen.queryByRole("link", { name: /series history/i })
    ).not.toBeInTheDocument()
  })
  it("does not link a series page to itself", async () => {
    vi.mocked(getHistory).mockResolvedValue(page())
    show()
    await screen.findByRole("table")
    expect(
      screen.queryByRole("navigation", { name: "Schedule series histories" })
    ).not.toBeInTheDocument()
    expect(
      screen.queryByRole("link", { name: /series history/i })
    ).not.toBeInTheDocument()
  })
  it.each([{ items: [] }, { items: [{ ...row, seriesId: null }] }])(
    "does not invent series links for empty or unassociated history: %j",
    async ({ items }) => {
      vi.mocked(getHistory).mockResolvedValue(page(items))
      show({}, { workflowId: row.workflowId })
      await waitFor(() =>
        expect(
          screen.queryByText("Loading run history...")
        ).not.toBeInTheDocument()
      )
      expect(
        screen.queryByRole("navigation", { name: "Schedule series histories" })
      ).not.toBeInTheDocument()
    }
  )
  it("keeps selected sources and schedule names across history pages", async () => {
    const otherSeriesId = "019ed132-2f44-7000-8000-000000000005"
    vi.mocked(getHistory)
      .mockResolvedValueOnce({ ...page([row], "next"), schedules })
      .mockResolvedValueOnce({
        ...page([
          { ...row, runId: "019ed132-2f44-7000-8000-000000000011" },
          {
            ...row,
            runId: "019ed132-2f44-7000-8000-000000000012",
            seriesId: otherSeriesId,
          },
        ]),
        schedules,
      })
    const search = {
      seriesIds: `${row.seriesId},${otherSeriesId}`,
      includeManual: "false",
    } as const
    show(search, { workflowId: row.workflowId })
    await screen.findByRole("table")
    fireEvent.click(screen.getByRole("button", { name: "Load more history" }))
    await waitFor(() =>
      expect(screen.getAllByRole("rowheader")).toHaveLength(3)
    )
    expect(screen.getAllByRole("rowheader")).toHaveLength(3)
    expect(
      screen.getByRole("cell", { name: "Full Test (paused)" })
    ).toBeVisible()
    expect(vi.mocked(getHistory).mock.calls.at(-1)?.[1]).toEqual(search)
  })
  it("retains named tracking context even when selected schedules have no matching history", async () => {
    vi.mocked(getHistory).mockResolvedValue({ ...page([]), schedules })
    show(
      { seriesIds: schedules[1]!.seriesId, includeManual: "false" },
      { workflowId: row.workflowId }
    )
    expect(await screen.findByText("No matching history.")).toBeVisible()
    expect(screen.getByLabelText("Tracked history sources")).toHaveTextContent(
      "Full Test (paused)"
    )
    expect(
      screen.getByLabelText("Tracked history sources")
    ).not.toHaveTextContent("Training")
  })
  it("labels execution UUIDs as Run ID in a separate column with their original run links", async () => {
    vi.mocked(getHistory).mockResolvedValue(page())
    show()
    const table = await screen.findByRole("table")
    expect(within(table).getAllByRole("columnheader")[1]).toHaveTextContent(
      /^Run ID$/
    )
    const run = within(table).getByRole("rowheader", { name: row.runId! })
    expect(within(run).getByRole("link", { name: row.runId! })).toHaveAttribute(
      "href",
      `/runs/${row.runId}`
    )
    expect(run.querySelector("time")).not.toBeInTheDocument()
    expect(run).not.toHaveTextContent(formatTimestamp(row.at))
  })
  it.each([
    ["2026-10-18T23:59:59.007999Z", "007"],
    ["2026-10-19T05:29:59.007999+05:30", "007"],
    ["2026-10-18T23:59:59Z", "000"],
  ])(
    "shows %s in a separate local date column with milliseconds",
    async (at, milliseconds) => {
      vi.mocked(getHistory).mockResolvedValue(page([{ ...row, at }]))
      show()
      const table = await screen.findByRole("table")
      expect(
        within(table).getByRole("columnheader", { name: "Date / time (local)" })
      ).toBeVisible()
      const date = within(within(table).getAllByRole("row")[1]!).getAllByRole(
        "cell"
      )[0]!
      expect(date).toHaveTextContent(formatTimestamp(at))
      expect(date.textContent).toContain(`.${milliseconds} `)
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
    expect(table).toHaveTextContent(formatTimestamp(row.abort!.at))
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
    expect(screen.getByRole("rowheader", { name: "No run" })).toBeVisible()
    expect(
      screen.queryByRole("link", { name: /^019ed132/ })
    ).not.toBeInTheDocument()
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
      show({}, { workflowId: row.workflowId })
      await screen.findByRole("table")
      expect(screen.getByLabelText("Tracked history sources")).toBeVisible()
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
      expect(
        screen.queryByLabelText("Tracked history sources")
      ).not.toBeInTheDocument()
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
  it("preserves selected schedule sources when applying or clearing ordinary history filters", async () => {
    const user = userEvent.setup()
    const onChange = vi.fn()
    const sources = {
      seriesIds: schedules[0]!.seriesId,
      includeManual: "false",
    } as const
    render(<HistoryFilters search={sources} onChange={onChange} />)
    await user.selectOptions(screen.getByLabelText("Run status"), "Succeeded")
    await user.click(screen.getByRole("button", { name: "Filter history" }))
    expect(onChange).toHaveBeenLastCalledWith({
      ...sources,
      status: "Succeeded",
      sort: "createdAtDesc",
    })
    await user.click(
      screen.getByRole("button", { name: "Clear history filters" })
    )
    expect(onChange).toHaveBeenLastCalledWith(sources)
  })
})
