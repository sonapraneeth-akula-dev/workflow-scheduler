import { beforeEach, describe, expect, it, vi } from "vitest"
import { getTimeline, instantTicks } from "./timeline-api"

const { get } = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { GET: get },
}))
const runId = "019ed132-2f44-7000-8000-000000000001"
const row = {
  nodeKey: "node",
  taskKey: "node/task",
  attempt: 1,
  queuedAt: "2026-10-16T08:00:00.0000001Z",
  dispatchedAt: "2026-10-16T08:00:00.0000002Z",
  startedAt: "2026-10-16T08:00:00.0000003Z",
  endedAt: "2026-10-16T08:00:00.0000004Z",
  queueWaitMs: 0.0002,
  durationMs: 0.0001,
  terminalStatus: "Failed",
  failureCategory: "TaskError",
  agentId: "agent-original",
  resumedFrom: null,
}
function respond(attempts: unknown[] = [row], identity = runId) {
  get.mockResolvedValue({
    response: new Response(),
    data: { runId: identity, lastSeq: 7, status: "Running", attempts },
  })
}
describe("committed timeline boundary", () => {
  beforeEach(() => vi.resetAllMocks())
  it("uses the typed authenticated API without query params and preserves sub-microsecond instants", async () => {
    respond()
    const signal = new AbortController().signal
    expect((await getTimeline(runId, signal)).attempts).toEqual([row])
    expect(get).toHaveBeenCalledWith("/api/v1/runs/{id}/timeline", {
      params: { path: { id: runId } },
      signal,
    })
    expect(instantTicks(row.endedAt) - instantTicks(row.startedAt)).toBe(1n)
    expect(instantTicks("2026-10-16T10:00:00.0000004+02:00")).toBe(
      instantTicks(row.endedAt)
    )
  })
  it("retains one row per retry and does not substitute a later/current agent", async () => {
    respond([
      row,
      {
        ...row,
        attempt: 2,
        agentId: "agent-retry",
        resumedFrom: row.startedAt,
        terminalStatus: "Succeeded",
        failureCategory: null,
      },
    ])
    expect(
      (await getTimeline(runId)).attempts.map((attempt) => [
        attempt.attempt,
        attempt.agentId,
      ])
    ).toEqual([
      [1, "agent-original"],
      [2, "agent-retry"],
    ])
  })
  it("keeps missing legacy attribution and active ends explicit without a ticking duration", async () => {
    respond([
      {
        ...row,
        agentId: null,
        endedAt: null,
        durationMs: 0,
        terminalStatus: null,
        failureCategory: null,
      },
    ])
    expect((await getTimeline(runId)).attempts[0]).toMatchObject({
      agentId: null,
      endedAt: null,
      durationMs: 0,
    })
    respond([])
    expect((await getTimeline(runId)).attempts).toEqual([])
  })
  it.each([
    { attempts: [], attempt: null, valid: true },
    { attempts: [row], attempt: 1, valid: true },
    { attempts: [row], attempt: null, valid: false },
    { attempts: [], attempt: 1, valid: false },
    { attempts: [row], attempt: 2, valid: false },
    {
      attempts: [
        { ...row, terminalStatus: "Succeeded", failureCategory: null },
      ],
      attempt: 1,
      valid: false,
    },
  ])(
    "validates task failure identity against actual dispatch evidence: %j",
    async ({ attempts, attempt, valid }) => {
      const failure = {
        type: "Infra",
        category: "Placement",
        message:
          "No eligible healthy worker became available. The task did not start.",
        nodeKey: "node",
        taskKey: "node/task",
        attempt,
        impact: "Failed",
        runtimeCategory: "Placement",
        ruleId: null,
        messageRecorded: false,
      }
      get.mockResolvedValue({
        response: new Response(),
        data: {
          runId,
          lastSeq: 7,
          status: "Failed",
          attempts,
          failures: [failure],
        },
      })
      if (valid) expect((await getTimeline(runId)).failures).toEqual([failure])
      else await expect(getTimeline(runId)).rejects.toThrow("inconsistent")
    }
  )
  it.each([
    { nodeKey: "other" },
    { taskKey: "invalid" },
    { attempt: 0 },
    { attempt: 32768 },
    { queuedAt: "invalid" },
    { queuedAt: "2026-10-16T08:00:00.00000001Z" },
    { queueWaitMs: -1 },
    { queueWaitMs: 100 },
    { durationMs: 100 },
    { terminalStatus: null },
    { failureCategory: "Unknown" },
    { agentId: "" },
    { resumedFrom: "invalid" },
    { resumedFrom: row.startedAt },
    { attempt: 2, resumedFrom: "1969-12-31T23:59:59Z" },
    { attempt: 2, resumedFrom: "2026-10-16T10:00:00+02:00" },
    { terminalStatus: "Succeeded", failureCategory: "TaskError" },
  ])(
    "rejects malformed or inconsistent attempt evidence: %j",
    async (changes) => {
      respond([{ ...row, ...changes }])
      await expect(getTimeline(runId)).rejects.toThrow()
    }
  )
  it("rejects duplicate/decreasing attempt numbers and cross-run responses", async () => {
    respond([row, row])
    await expect(getTimeline(runId)).rejects.toThrow("inconsistent")
    respond([{ ...row, attempt: 2 }, row])
    await expect(getTimeline(runId)).rejects.toThrow("inconsistent")
    respond([row], "019ed132-2f44-7000-8000-000000000002")
    await expect(getTimeline(runId)).rejects.toThrow("inconsistent")
  })
  it("preserves stored zero-clamped metrics for reversed recorded clocks without rewriting the timestamps", async () => {
    respond([
      {
        ...row,
        queuedAt: row.endedAt,
        dispatchedAt: row.startedAt,
        startedAt: row.endedAt,
        endedAt: row.startedAt,
        queueWaitMs: 0,
        durationMs: 0,
      },
    ])
    expect((await getTimeline(runId)).attempts[0]?.endedAt).toBe(row.startedAt)
  })
  it("strips unrelated private values and fails explicitly on missing required arrays or denied reads", async () => {
    respond([{ ...row, variables: { secret: "private" }, outputs: "private" }])
    expect(JSON.stringify(await getTimeline(runId))).not.toContain("private")
    respond([{ ...row, resumedFrom: undefined }])
    await expect(getTimeline(runId)).rejects.toThrow()
    get.mockResolvedValue({
      response: new Response(),
      data: { runId, lastSeq: 0, status: "Queued" },
    })
    await expect(getTimeline(runId)).rejects.toThrow()
    get.mockResolvedValue({
      response: new Response(null, { status: 403 }),
      error: { title: "Viewer denied." },
    })
    await expect(getTimeline(runId)).rejects.toThrow("Viewer denied.")
  })
  it.each([
    "invalid",
    "11111111-1111-4111-8111-111111111111",
    "019ED132-2F44-7000-8000-000000000001",
  ])("rejects invalid run identity %s before network access", async (id) => {
    await expect(getTimeline(id)).rejects.toThrow()
    expect(get).not.toHaveBeenCalled()
  })
})
