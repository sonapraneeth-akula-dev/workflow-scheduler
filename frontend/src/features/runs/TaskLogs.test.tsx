import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import { useLive } from "@/lib/live/useLive"
import type { LiveSnapshot } from "@/lib/live/subscriptions"
import { getTimeline, type RunTimeline } from "./timeline-api"
import { getTaskLogs, type TaskLogsResult } from "./logs-api"
import { TaskLogs } from "./TaskLogs"

vi.mock("./timeline-api", () => ({ getTimeline: vi.fn() }))
vi.mock("./logs-api", async (original) => ({
  ...(await original<typeof import("./logs-api")>()),
  getTaskLogs: vi.fn(),
}))
vi.mock("@/lib/live/useLive", () => ({ useLive: vi.fn() }))
const runId = "019ed132-2f44-7000-8000-000000000001"
const row = {
  nodeKey: "work",
  taskKey: "work/task",
  attempt: 1,
  queuedAt: "2026-10-18T00:00:00Z",
  dispatchedAt: "2026-10-18T00:00:01Z",
  startedAt: "2026-10-18T00:00:02Z",
  endedAt: "2026-10-18T00:00:04Z",
  queueWaitMs: 2000,
  durationMs: 2000,
  terminalStatus: "Succeeded" as const,
  failureCategory: null,
  agentId: null,
  resumedFrom: null,
}
const timeline: RunTimeline = {
  runId,
  lastSeq: 10,
  status: "Succeeded",
  attempts: [row],
}
const frame = {
  at: row.startedAt,
  stream: "stdout" as const,
  level: "Information" as const,
  text: "masked ***",
}
let snapshot: LiveSnapshot
let runSnapshot: LiveSnapshot

function result(offset = 0, length = 400, attempt = 1): TaskLogsResult {
  return {
    state: "ready",
    page: {
      totalLines: 1000000,
      droppedLines: 0,
      truncated: false,
      captureInterrupted: false,
      truncationMarker: null,
      matches: [],
      nextCursor: null,
      records: Array.from(
        { length: Math.min(length, 1000000 - offset) },
        (_, index) => ({
          line: offset + index + 1,
          frame: {
            ...frame,
            text: `attempt ${attempt} record ${offset + index + 1} <script>***</script>`,
          },
        })
      ),
    },
  }
}
function show() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  const content = () => (
    <QueryClientProvider client={client}>
      <TaskLogs runId={runId} />
    </QueryClientProvider>
  )
  const rendered = render(content())
  return { ...rendered, refresh: () => rendered.rerender(content()) }
}

describe("virtualized per-attempt task logs", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    snapshot = {
      resource: { scope: "logs", id: `${runId}/work/task/1` },
      status: "live",
      error: null,
      delta: null,
      logDelta: null,
      resyncVersion: 0,
      reportVersion: 0,
    }
    runSnapshot = {
      ...snapshot,
      resource: { scope: "run", id: runId },
    }
    vi.mocked(useLive).mockImplementation((resource) => ({
      snapshot: resource?.scope === "run" ? runSnapshot : snapshot,
      retry: vi.fn(),
    }))
    vi.mocked(getTimeline).mockResolvedValue(timeline)
    vi.mocked(getTaskLogs).mockImplementation(
      async (_, selected, offset, length) =>
        result(offset, length, selected.attempt)
    )
  })
  it("opens a million-line log without mounting its rows and seeks by keyboard using bounded ranges", async () => {
    const { container } = show()
    const viewport = await screen.findByRole("log", { name: "Task log lines" })
    await screen.findByText("attempt 1 record 1 <script>***</script>", {
      exact: false,
    })
    expect(
      container.querySelectorAll("[data-log-line]").length
    ).toBeLessThanOrEqual(36)
    expect(container.querySelector("script")).toBeNull()
    expect(viewport.firstElementChild).toHaveStyle({ height: "24000000px" })
    viewport.focus()
    fireEvent.keyDown(viewport, { key: "End" })
    await screen.findByText("attempt 1 record 1000000 <script>***</script>", {
      exact: false,
    })
    expect(viewport).toHaveFocus()
    expect(
      container.querySelectorAll("[data-log-line]").length
    ).toBeLessThanOrEqual(36)
    expect(getTaskLogs).toHaveBeenLastCalledWith(
      runId,
      expect.objectContaining({ attempt: 1 }),
      999800,
      400,
      expect.any(AbortSignal)
    )
  })
  it("clears previous retry bytes immediately while the selected historical attempt loads", async () => {
    vi.mocked(getTimeline).mockResolvedValue({
      ...timeline,
      attempts: [row, { ...row, attempt: 2 }],
    })
    let finish: ((value: TaskLogsResult) => void) | undefined
    vi.mocked(getTaskLogs).mockImplementation(async (_, selected) =>
      selected.attempt === 2
        ? result(0, 400, 2)
        : new Promise((resolve) => {
            finish = resolve
          })
    )
    show()
    await screen.findByText("attempt 2 record 1 <script>***</script>", {
      exact: false,
    })
    fireEvent.change(screen.getByLabelText("Log attempt"), {
      target: { value: '["work/task",1]' },
    })
    expect(screen.queryByText(/attempt 2 record/)).not.toBeInTheDocument()
    await waitFor(() => expect(finish).toBeDefined())
    await act(async () => finish?.(result()))
    expect(
      await screen.findByText("attempt 1 record 1 <script>***</script>", {
        exact: false,
      })
    ).toBeVisible()
  })
  it("hides all cached archived and live bytes on authorization failure", async () => {
    const rendered = show()
    await screen.findByText("attempt 1 record 1 <script>***</script>", {
      exact: false,
    })
    snapshot = {
      ...snapshot,
      status: "error",
      error: new ApiError(new Response(null, { status: 403 }), {
        title: "Forbidden",
      }),
    }
    rendered.refresh()
    expect(screen.queryByRole("log")).not.toBeInTheDocument()
    expect(
      screen.getByText(
        "Previously loaded logs are hidden until access is authorized again."
      )
    ).toBeVisible()
    expect(screen.queryByText(/attempt 1 record/)).not.toBeInTheDocument()
  })
  it("shows live records and explicit gaps, and releases interest when paused", async () => {
    vi.mocked(getTaskLogs).mockResolvedValue({ state: "uploading" })
    const rendered = show()
    await screen.findByRole("log")
    snapshot = {
      ...snapshot,
      logDelta: {
        id: snapshot.resource.id,
        records: [{ ...frame, line: 1 }],
        gap: true,
      },
    }
    rendered.refresh()
    expect(
      await screen.findByText("masked ***", { exact: false })
    ).toBeVisible()
    expect(screen.getByText(/Live log gap detected/)).toBeVisible()
    fireEvent.click(screen.getByRole("button", { name: "Pause live logs" }))
    expect(useLive).toHaveBeenLastCalledWith(undefined)
  })
  it("discovers new retries on committed transitions and resynchronization without polling", async () => {
    const rendered = show()
    await screen.findByText("attempt 1 record 1 <script>***</script>", {
      exact: false,
    })
    await waitFor(() => expect(getTimeline).toHaveBeenCalledTimes(2))
    vi.mocked(getTimeline).mockResolvedValue({
      ...timeline,
      attempts: [row, { ...row, attempt: 2 }],
    })
    runSnapshot = { ...runSnapshot, resyncVersion: 1 }
    rendered.refresh()
    await screen.findByText("attempt 2 record 1 <script>***</script>", {
      exact: false,
    })
    expect(getTimeline).toHaveBeenCalledTimes(3)
    rendered.refresh()
    expect(getTimeline).toHaveBeenCalledTimes(3)
  })
  it("unmounts log interest and all attempt bytes when the run subscription loses permission", async () => {
    const rendered = show()
    await screen.findByRole("log")
    runSnapshot = {
      ...runSnapshot,
      status: "error",
      error: new ApiError(new Response(null, { status: 403 }), {
        title: "Forbidden",
      }),
    }
    rendered.refresh()
    expect(screen.queryByRole("log")).not.toBeInTheDocument()
    expect(screen.queryByLabelText("Log attempt")).not.toBeInTheDocument()
    expect(screen.queryByText(/attempt 1 record/)).not.toBeInTheDocument()
  })
})
