import { beforeEach, describe, expect, it, vi } from "vitest"
import { readRunEvents, readRunSnapshot } from "./runApi"

const { get } = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { GET: get },
}))

const runId = "019ed132-2f44-7000-8000-000000000001"
const event = {
  seq: 1,
  at: "2026-10-16T02:00:00Z",
  entity: 0,
  key: null,
  state: 1,
  abort: null,
}
const snapshot = {
  runId,
  workflowId: "team/type/workflow",
  versionId: "019ed132-2f44-7000-8000-000000000002",
  seriesId: null,
  status: 1,
  lastSeq: 1,
  nodes: { build: 3 },
  tasks: { "build/task": 3 },
  abort: null,
  endedAt: null,
  parentRunId: null,
  reusedNodes: {},
}
const signal = new AbortController().signal
const respond = (data: unknown) =>
  get.mockResolvedValue({ data, response: new Response() })

describe("run resync API boundaries", () => {
  beforeEach(() => vi.resetAllMocks())

  it("reads only the requested safe authoritative state", async () => {
    respond({
      ...snapshot,
      variables: { password: "must not enter the store" },
    })
    expect(await readRunSnapshot(runId, signal)).toEqual(snapshot)
  })

  it.each([
    { ...snapshot, runId: snapshot.versionId },
    { ...snapshot, lastSeq: Number.MAX_SAFE_INTEGER + 1 },
    { ...snapshot, tasks: { "foreign/task": 0 } },
    { ...snapshot, status: 7 },
    { ...snapshot, abort: { reason: "missing identity" } },
    { ...snapshot, parentRunId: runId },
    { ...snapshot, parentRunId: "https://foreign.invalid" },
    { ...snapshot, parentRunId: snapshot.versionId, nodes: { build: 11 } },
    {
      ...snapshot,
      parentRunId: snapshot.versionId,
      reusedNodes: { build: snapshot.versionId },
    },
    {
      ...snapshot,
      nodes: { build: 11 },
      reusedNodes: { build: snapshot.versionId },
    },
    {
      ...snapshot,
      parentRunId: snapshot.versionId,
      nodes: { build: 11 },
      reusedNodes: { build: runId },
    },
  ])("rejects unsafe or mismatched run state", async (body) => {
    respond(body)
    await expect(readRunSnapshot(runId, signal)).rejects.toThrow()
  })

  it("keeps exact lineage for reused nodes and strips private source outputs", async () => {
    const expected = {
      ...snapshot,
      parentRunId: snapshot.versionId,
      nodes: { build: 11 },
      tasks: { "build/task": 11 },
      reusedNodes: { build: snapshot.versionId },
    }
    respond({ ...expected, outputs: { private: "never in store" } })
    expect(await readRunSnapshot(runId, signal)).toEqual(expected)
  })

  it("requests exact decimal sequences, including the highest safe integer", async () => {
    respond({ runId, lastSeq: Number.MAX_SAFE_INTEGER, events: [] })
    expect(await readRunEvents(runId, Number.MAX_SAFE_INTEGER, signal)).toEqual(
      {
        runId,
        lastSeq: Number.MAX_SAFE_INTEGER,
        events: [],
      }
    )
    expect(get).toHaveBeenCalledWith("/api/v1/runs/{id}/events", {
      params: { path: { id: runId }, query: { afterSeq: "9007199254740991" } },
      signal,
    })
  })

  it.each([-1, 1.5, Number.MAX_SAFE_INTEGER + 1, Number.NaN])(
    "never sends an unsafe cursor %j",
    async (value) => {
      await expect(readRunEvents(runId, value, signal)).rejects.toThrow()
      expect(get).not.toHaveBeenCalled()
    }
  )

  it("accepts the complete contiguous public live event contract", async () => {
    respond({ runId, lastSeq: 1, events: [event] })
    expect(await readRunEvents(runId, 0, signal)).toEqual({
      runId,
      lastSeq: 1,
      events: [event],
    })
  })

  it.each([
    { runId: snapshot.versionId, lastSeq: 1, events: [event] },
    { runId, lastSeq: 2, events: [event] },
    { runId, lastSeq: 1, events: [{ ...event, seq: 2 }] },
    { runId, lastSeq: 1, events: [{ ...event, key: "not-a-run-key" }] },
    { runId, lastSeq: 1, events: [{ ...event, state: 7 }] },
    { runId, lastSeq: 1, events: [{ ...event, entity: 2, key: null }] },
    { runId, lastSeq: 1, events: [{ ...event, at: "invalid" }] },
    { runId, lastSeq: Number.MAX_SAFE_INTEGER + 1, events: [] },
    {
      runId,
      lastSeq: 5001,
      events: Array.from({ length: 5001 }, (_, i) => ({
        ...event,
        seq: i + 1,
      })),
    },
  ])("rejects incomplete, invalid or unbounded event pages", async (body) => {
    respond(body)
    await expect(readRunEvents(runId, 0, signal)).rejects.toThrow()
  })

  it.each([401, 403, 409, 500, 503])(
    "preserves HTTP %i for explicit recovery or errors",
    async (status) => {
      get.mockResolvedValue({
        response: new Response(null, { status }),
        error: { title: "Run unavailable" },
      })
      await expect(readRunEvents(runId, 0, signal)).rejects.toMatchObject({
        status,
        message: "Run unavailable",
      })
    }
  )
})
