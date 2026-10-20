import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { fireEvent, render, screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import type { ReactNode } from "react"
import { beforeEach, describe, expect, it, vi } from "vitest"
import type { RunSnapshot } from "@/lib/live/runApi"
import { FailureMessage, FailureSummary } from "./FailureSummary"
import {
  failureDetailSchema,
  failureLabels,
  failureSummarySchema,
  type FailureDetail,
  type FailureSummaryData,
} from "./failures"
import { RunFailures } from "./RunFailures"
import { getTimeline } from "./timeline-api"

vi.mock("./timeline-api", () => ({ getTimeline: vi.fn() }))
const failure: FailureDetail = {
  type: "Infra",
  category: "Network",
  message:
    "Synthetic infrastructure network failure: <script>not HTML</script>",
  nodeKey: "fetch",
  taskKey: "fetch/request",
  attempt: 1,
  impact: "Recovered",
  runtimeCategory: "TaskError",
  ruleId: "system-infra/demo-network",
  messageRecorded: true,
}
const summary: FailureSummaryData = {
  count: 2,
  recoveredCount: 2,
  primary: failure,
  groups: [
    { type: "Infra", category: "Network", impact: "Recovered", count: 2 },
  ],
}
const run: RunSnapshot = {
  runId: "019ed132-2f44-7000-8000-000000000001",
  workflowId: "demo/release/json-failure-demo",
  versionId: "019ed132-2f44-7000-8000-000000000002",
  seriesId: null,
  status: 3,
  lastSeq: 12,
  nodes: { fetch: 5 },
  tasks: { "fetch/request": 5 },
  abort: null,
  endedAt: "2026-10-19T12:00:00Z",
  parentRunId: null,
  reusedNodes: {},
}
function show(snapshot = run) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  )
  return render(<RunFailures run={snapshot} />, { wrapper })
}

describe("failure diagnostics", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    vi.mocked(getTimeline).mockResolvedValue({
      runId: run.runId,
      lastSeq: 12,
      status: "Succeeded",
      attempts: [],
      failures: [failure, { ...failure, attempt: 2 }],
      failureSummary: summary,
    })
  })
  it("validates attempt identity, recovery counts and complete groups", () => {
    expect(failureSummarySchema.parse(summary)).toEqual(summary)
    expect(failureLabels(summary)).toBe("Infra / Network (Recovered x2)")
    for (const invalid of [
      { ...failure, taskKey: null },
      { ...failure, taskKey: "other/request" },
      { ...failure, type: "Unknown" },
      { ...failure, message: "" },
    ])
      expect(failureDetailSchema.safeParse(invalid).success).toBe(false)
    for (const invalid of [
      { ...summary, count: 3 },
      { ...summary, recoveredCount: 1 },
      { ...summary, groups: [] },
      { ...summary, primary: { ...failure, category: "Storage" } },
    ])
      expect(failureSummarySchema.safeParse(invalid).success).toBe(false)
    expect(
      failureDetailSchema.parse({ ...failure, taskKey: null, attempt: null })
    ).toMatchObject({ taskKey: null, attempt: null })
  })
  it("renders diagnostic text safely and keeps mapping separate from scheduler category", () => {
    const { container } = render(<FailureMessage failure={failure} />)
    expect(screen.getByText(failure.message)).toBeVisible()
    expect(container.querySelector("script")).toBeNull()
    expect(screen.getByText(/Scheduler category: TaskError/)).toHaveTextContent(
      "Mapping: system-infra/demo-network."
    )
  })
  it("shows undispatched placement failures without inventing an execution attempt or truncating the reason", () => {
    const placement: FailureDetail = {
      ...failure,
      type: "Infra",
      category: "Placement",
      runtimeCategory: "Placement",
      attempt: null,
      impact: "Failed",
      ruleId: null,
      messageRecorded: false,
      message:
        "No eligible healthy worker became available before the placement deadline. The task did not start.",
    }
    expect(failureDetailSchema.parse(placement)).toEqual(placement)
    const placementSummary: FailureSummaryData = {
      count: 1,
      recoveredCount: 0,
      primary: placement,
      groups: [
        { type: "Infra", category: "Placement", impact: "Failed", count: 1 },
      ],
    }
    render(<FailureSummary status="Failed" summary={placementSummary} />)
    expect(screen.getByText("Infra / Placement (Failed)")).toBeVisible()
    expect(screen.getByText(placement.message)).not.toHaveClass("truncate")
    expect(screen.getByText("Task: fetch/request (not started)")).toBeVisible()
  })
  it("labels undispatched task diagnostics separately from node failures", async () => {
    vi.mocked(getTimeline).mockResolvedValue({
      runId: run.runId,
      lastSeq: 12,
      status: "Failed",
      attempts: [],
      failures: [{ ...failure, attempt: null, impact: "Failed" }],
      failureSummary: null,
    })
    show({ ...run, status: 4 })
    await userEvent.click(await screen.findByText(/All recorded failures/))
    expect(await screen.findByText("fetch/request - not started")).toBeVisible()
    expect(screen.queryByText(/node failure/)).not.toBeInTheDocument()
    expect(screen.queryByText(/attempt 1/)).not.toBeInTheDocument()
  })
  it("distinguishes old backends from missing recorded failure details", () => {
    const { rerender } = render(<FailureSummary status="Failed" />)
    expect(
      screen.getByText("Failure details unavailable from this backend.")
    ).toBeVisible()
    rerender(<FailureSummary status="TimedOut" summary={null} />)
    expect(screen.getByText("No failure details recorded.")).toBeVisible()
    rerender(<FailureSummary status="Succeeded" summary={summary} />)
    expect(screen.getByText("Infra / Network (Recovered x2)")).toBeVisible()
  })
  it("loads terminal failures once and lazily shows every recovered attempt", async () => {
    show()
    await screen.findByText("Infra / Network (Recovered x2)")
    expect(getTimeline).toHaveBeenCalledTimes(1)
    expect(
      screen.queryByText("fetch/request - attempt 1")
    ).not.toBeInTheDocument()
    await userEvent.click(screen.getByText(/All recorded failures/))
    expect(await screen.findByText("fetch/request - attempt 1")).toBeVisible()
    expect(screen.getByText("fetch/request - attempt 2")).toBeVisible()
  })
  it("does not automatically load active-run attempts; explicit refresh remains available", async () => {
    show({ ...run, status: 1, endedAt: null })
    expect(getTimeline).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole("button", { name: "Refresh failures" }))
    await screen.findByText("Infra / Network (Recovered x2)")
    expect(getTimeline).toHaveBeenCalledTimes(1)
  })
  it("rejects stale terminal history instead of showing a success-shaped fallback", async () => {
    vi.mocked(getTimeline).mockResolvedValue({
      runId: run.runId,
      lastSeq: 11,
      status: "Running",
      attempts: [],
    })
    show()
    expect(
      await screen.findByText(
        "The committed attempt history has not reached the terminal run state."
      )
    ).toBeVisible()
    expect(
      screen.queryByText("No recorded execution failures.")
    ).not.toBeInTheDocument()
  })
  it("reloads the final committed history after an active-run manual refresh", async () => {
    vi.mocked(getTimeline).mockResolvedValueOnce({
      runId: run.runId,
      lastSeq: 10,
      status: "Running",
      attempts: [],
      failures: [],
    })
    const { rerender } = show({ ...run, status: 1, endedAt: null })
    fireEvent.click(screen.getByRole("button", { name: "Refresh failures" }))
    await screen.findByText("No recorded execution failures.")
    rerender(<RunFailures run={run} />)
    await screen.findByText("Infra / Network (Recovered x2)")
    expect(getTimeline).toHaveBeenCalledTimes(2)
    expect(
      screen.queryByText("No recorded execution failures.")
    ).not.toBeInTheDocument()
  })
})
