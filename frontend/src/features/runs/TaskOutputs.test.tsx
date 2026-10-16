import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import { getTimeline, type RunTimeline } from "./timeline-api"
import { getTaskOutputs, type TaskOutputsResult } from "./outputs-api"
import { TaskOutputs } from "./TaskOutputs"

vi.mock("./timeline-api", () => ({ getTimeline: vi.fn() }))
vi.mock("./outputs-api", () => ({ getTaskOutputs: vi.fn() }))
const runId = "019ed132-2f44-7000-8000-000000000001"
const row = {
  nodeKey: "work",
  taskKey: "work/task",
  attempt: 1,
  queuedAt: "2026-10-16T08:00:00Z",
  dispatchedAt: "2026-10-16T08:00:01Z",
  startedAt: "2026-10-16T08:00:02Z",
  endedAt: "2026-10-16T08:00:04Z",
  queueWaitMs: 2000,
  durationMs: 2000,
  terminalStatus: "Failed" as const,
  failureCategory: "TaskError" as const,
  agentId: null,
}
const timeline: RunTimeline = {
  runId,
  lastSeq: 12,
  status: "Running",
  attempts: [
    row,
    { ...row, attempt: 2, terminalStatus: null, endedAt: null, durationMs: 0 },
  ],
}
const result: TaskOutputsResult = {
  runId,
  taskKey: row.taskKey,
  attempt: 1,
  outputs: { html: "<script>alert(1)</script>", null: null },
}
function show() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  return render(
    <QueryClientProvider client={client}>
      <TaskOutputs runId={runId} />
    </QueryClientProvider>
  )
}
describe("TaskOutputs", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    vi.mocked(getTimeline).mockResolvedValue(timeline)
    vi.mocked(getTaskOutputs).mockResolvedValue(result)
  })
  it("renders JSON as text, not executable HTML, with labeled keyboard focus", async () => {
    const user = userEvent.setup()
    const { container } = show()
    const json = await screen.findByLabelText("Attempt JSON outputs")
    expect(json).toHaveTextContent("<script>alert(1)</script>")
    expect(json).toHaveTextContent('"null": null')
    expect(container.querySelector("script")).toBeNull()
    expect(screen.getByLabelText("Output attempt")).toHaveValue(
      '["work/task",1]'
    )
    json.focus()
    expect(json).toHaveFocus()
    await user.tab({ shift: true })
    expect(screen.getByLabelText("Output attempt")).toHaveFocus()
  })
  it("clears old outputs immediately on selection and retains independent retry snapshots", async () => {
    let resolve: ((value: TaskOutputsResult) => void) | undefined
    vi.mocked(getTaskOutputs).mockImplementation(async (_, selected) =>
      selected.attempt === 1
        ? result
        : new Promise((done) => {
            resolve = done
          })
    )
    show()
    await screen.findByLabelText("Attempt JSON outputs")
    fireEvent.change(screen.getByLabelText("Output attempt"), {
      target: { value: '["work/task",2]' },
    })
    expect(
      screen.queryByLabelText("Attempt JSON outputs")
    ).not.toBeInTheDocument()
    expect(screen.getByText("Loading task outputs...")).toBeVisible()
    await waitFor(() => expect(resolve).toBeDefined())
    await act(async () => resolve!({ ...result, attempt: 2, outputs: null }))
    expect(
      await screen.findByText(
        "No structured outputs recorded for this attempt."
      )
    ).toBeVisible()
    fireEvent.change(screen.getByLabelText("Output attempt"), {
      target: { value: '["work/task",1]' },
    })
    expect(
      await screen.findByLabelText("Attempt JSON outputs")
    ).toHaveTextContent("alert")
  })
  it("distinguishes an empty JSON object from absent outputs", async () => {
    vi.mocked(getTaskOutputs).mockResolvedValue({ ...result, outputs: {} })
    show()
    expect(
      await screen.findByLabelText("Attempt JSON outputs")
    ).toHaveTextContent("{}")
    expect(
      screen.queryByText("No structured outputs recorded for this attempt.")
    ).not.toBeInTheDocument()
  })
  it("loads and announces empty history without requesting invented attempts", async () => {
    vi.mocked(getTimeline).mockResolvedValue({ ...timeline, attempts: [] })
    show()
    expect(screen.getByText("Loading task outputs...")).toBeVisible()
    expect(
      await screen.findByText("No dispatched attempts recorded.")
    ).toBeVisible()
    expect(getTaskOutputs).not.toHaveBeenCalled()
  })
  it.each([401, 403, 503])(
    "hides stale output on failed refresh %i",
    async (status) => {
      show()
      await screen.findByLabelText("Attempt JSON outputs")
      vi.mocked(getTaskOutputs).mockRejectedValue(
        new ApiError(new Response(null, { status }), {
          title: "Output access failed",
        })
      )
      fireEvent.click(
        screen.getByRole("button", { name: "Refresh task outputs" })
      )
      await screen.findByText("Output access failed")
      expect(
        screen.queryByLabelText("Attempt JSON outputs")
      ).not.toBeInTheDocument()
      if (status !== 503)
        expect(
          screen.getByText(/Previously loaded outputs are hidden/)
        ).toBeVisible()
    }
  )
  it("rechecks new committed outputs while an attempt completes during the run", async () => {
    vi.mocked(getTaskOutputs)
      .mockResolvedValueOnce({ ...result, outputs: null })
      .mockResolvedValue(result)
    show()
    await screen.findByText("No structured outputs recorded for this attempt.")
    fireEvent.click(
      screen.getByRole("button", { name: "Refresh task outputs" })
    )
    expect(
      await screen.findByLabelText("Attempt JSON outputs")
    ).toHaveTextContent("alert")
    expect(getTimeline).toHaveBeenCalledTimes(2)
  })
})
