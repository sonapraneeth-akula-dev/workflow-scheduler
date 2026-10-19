import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { getVersionPlan, type VersionPlan } from "@/features/taskplan/api"
import { getTimeline, type RunTimeline } from "@/features/runs/timeline-api"
import { getTaskOutputs } from "@/features/runs/outputs-api"
import { ApiError } from "@/lib/api/client"
import type { RunSnapshot } from "@/lib/live/runApi"
import { LiveRunGraph } from "./RunGraph"

vi.mock("@/features/taskplan/api", () => ({ getVersionPlan: vi.fn() }))
vi.mock("@/features/runs/timeline-api", () => ({ getTimeline: vi.fn() }))
vi.mock("@/features/runs/outputs-api", () => ({ getTaskOutputs: vi.fn() }))
vi.mock("@tanstack/react-router", () => ({
  Link: ({ children }: { children: React.ReactNode }) => (
    <span>{children}</span>
  ),
}))
const run: RunSnapshot = {
  runId: "019ed132-2f44-7000-8000-000000000002",
  versionId: "019ed132-2f44-7000-8000-000000000001",
  workflowId: "demo/release/workflow",
  seriesId: null,
  status: 1,
  lastSeq: 12,
  nodes: { fetch: 3 },
  tasks: { "fetch/request": 3 },
  endedAt: null,
  parentRunId: null,
  reusedNodes: {},
  abort: null,
}
const plan: VersionPlan = {
  versionId: run.versionId,
  workflowId: run.workflowId,
  version: "1.0.0",
  runTimeout: "01:00:00",
  stages: [],
  placement: null,
  nodes: [
    {
      key: "fetch",
      mode: "Series",
      failure: "Hard",
      maxParallel: null,
      dependencies: [],
      placement: null,
      timeout: null,
      tasks: [
        {
          key: "request",
          failure: "Hard",
          placement: null,
          action: { kind: "SdkFunction", declaration: "Fetch" },
          secretReference: null,
          requestTimeout: null,
          policy: {
            attemptTimeout: "00:01:00",
            autoRetry: false,
            retry: {
              retries: 2,
              on: ["TaskError"],
              backoff: {
                kind: "Fixed",
                delay: "00:00:03",
                maxDelay: "00:00:03",
              },
            },
          },
        },
      ],
    },
  ],
}
const row: RunTimeline["attempts"][number] = {
  nodeKey: "fetch",
  taskKey: "fetch/request",
  attempt: 1,
  queuedAt: "2026-10-19T08:00:00Z",
  dispatchedAt: "2026-10-19T08:00:00Z",
  startedAt: "2026-10-19T08:00:00Z",
  endedAt: "2026-10-19T08:00:01Z",
  queueWaitMs: 0,
  durationMs: 1000,
  terminalStatus: "Failed",
  failureCategory: "TaskError",
  agentId: null,
  resumedFrom: null,
}
const timeline: RunTimeline = {
  runId: run.runId,
  lastSeq: 20,
  status: "Succeeded",
  attempts: [
    row,
    { ...row, attempt: 2 },
    { ...row, attempt: 3, terminalStatus: "Succeeded", failureCategory: null },
  ],
}
function show(snapshot = run) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  const wrap = (value: RunSnapshot) => (
    <QueryClientProvider client={client}>
      <LiveRunGraph run={value} />
    </QueryClientProvider>
  )
  const result = render(wrap(snapshot))
  return {
    ...result,
    update: (value: RunSnapshot) => result.rerender(wrap(value)),
  }
}

describe("recorded graph history and lazy node dataflow", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    vi.mocked(getVersionPlan).mockResolvedValue(plan)
    vi.mocked(getTimeline).mockResolvedValue(timeline)
    vi.mocked(getTaskOutputs).mockResolvedValue({
      runId: run.runId,
      taskKey: row.taskKey,
      attempt: 3,
      outputs: {
        request: { inputs: { count: 3 }, outputs: { status: "Succeeded" } },
      },
    })
  })
  it("makes no history or output requests across active live frames or closed inspectors", async () => {
    const { update } = show()
    fireEvent.click(
      await screen.findByRole("button", { name: "fetch: Running" })
    )
    for (let lastSeq = 13; lastSeq < 113; lastSeq++) update({ ...run, lastSeq })
    expect(getVersionPlan).toHaveBeenCalledTimes(1)
    expect(getTimeline).not.toHaveBeenCalled()
    expect(getTaskOutputs).not.toHaveBeenCalled()
  })
  it("loads terminal history once and distinguishes actual retries from configuration", async () => {
    const terminal = {
      ...run,
      status: 3,
      nodes: { fetch: 5 },
      tasks: { "fetch/request": 5 },
    }
    const { update } = show(terminal)
    const node = await screen.findByRole("button", { name: "fetch: Succeeded" })
    await waitFor(() =>
      expect(node).toHaveAccessibleDescription(/Recorded retries: 2/)
    )
    expect(within(node).getByTitle("2 recorded retries")).toHaveTextContent("2")
    expect(node).toHaveAccessibleDescription(/Failure retries: 1\/1 tasks/)
    update({ ...terminal, lastSeq: 13 })
    expect(getTimeline).toHaveBeenCalledTimes(1)
    expect(getTaskOutputs).not.toHaveBeenCalled()
  })
  it("does not treat configured retries as observed retries", async () => {
    vi.mocked(getTimeline).mockResolvedValue({ ...timeline, attempts: [row] })
    show({
      ...run,
      status: 4,
      nodes: { fetch: 7 },
      tasks: { "fetch/request": 7 },
    })
    const node = await screen.findByRole("button", { name: "fetch: Failed" })
    await waitFor(() =>
      expect(node).toHaveAccessibleDescription(/Recorded retries: 0/)
    )
    expect(
      within(node).queryByTitle(/recorded retries/)
    ).not.toBeInTheDocument()
  })
  it("loads exact selected-node attempts only after opening dataflow; denied refresh hides payloads", async () => {
    show()
    fireEvent.click(
      await screen.findByRole("button", { name: "fetch: Running" })
    )
    const inspector = screen.getByRole("region", {
      name: "Selected node fetch",
    })
    const details = within(inspector)
      .getByText("Inspect recorded inputs / outputs")
      .closest("details")!
    details.open = true
    fireEvent(details, new Event("toggle"))
    expect(await screen.findByLabelText("Attempt JSON outputs")).toBeVisible()
    expect(getTaskOutputs).toHaveBeenCalledWith(
      run.runId,
      expect.objectContaining({ taskKey: row.taskKey, attempt: 3 }),
      expect.any(AbortSignal)
    )
    vi.mocked(getTaskOutputs).mockRejectedValue(
      new ApiError(new Response(null, { status: 403 }), {
        title: "Viewer denied.",
      })
    )
    fireEvent.click(
      screen.getByRole("button", { name: "Refresh task outputs" })
    )
    expect(await screen.findByText("Viewer denied.")).toBeVisible()
    expect(
      screen.queryByLabelText("Attempt JSON outputs")
    ).not.toBeInTheDocument()
  })
  it("rejects incomplete terminal history instead of inventing zero retries", async () => {
    vi.mocked(getTimeline).mockResolvedValue({
      ...timeline,
      status: "Running",
      lastSeq: 1,
    })
    show({
      ...run,
      status: 3,
      nodes: { fetch: 5 },
      tasks: { "fetch/request": 5 },
    })
    expect(
      await screen.findByText(
        "The committed attempt history has not reached the terminal run state."
      )
    ).toBeVisible()
    expect(
      await screen.findByRole("button", { name: "fetch: Succeeded" })
    ).not.toHaveAccessibleDescription(/Recorded retries/)
    expect(
      screen.getByRole("button", { name: "Retry recorded attempt history" })
    ).toBeVisible()
  })
  it("keeps reused node data in its source run rather than fetching substituted attempts", async () => {
    show({
      ...run,
      nodes: { fetch: 11 },
      tasks: { "fetch/request": 11 },
      parentRunId: run.versionId,
      reusedNodes: { fetch: run.versionId },
    })
    fireEvent.click(
      await screen.findByRole("button", { name: "fetch: Reused" })
    )
    expect(
      screen.getByText(/No original attempt data is substituted/)
    ).toBeVisible()
    expect(
      screen.queryByText("Inspect recorded inputs / outputs")
    ).not.toBeInTheDocument()
    expect(getTimeline).not.toHaveBeenCalled()
  })
})
