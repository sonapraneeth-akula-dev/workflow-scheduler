import { beforeEach, describe, expect, it, vi } from "vitest"
import { getTaskLogs } from "./logs-api"
import { appendLogTail, emptyLogTail } from "./log-tail"
import { parseLogBatch } from "@/lib/live/logs"

const { get } = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { GET: get },
}))
const runId = "019ed132-2f44-7000-8000-000000000001"
const attempt = { taskKey: "work/task", attempt: 2 }
const id = `${runId}/${attempt.taskKey}/2`
const frame = {
  at: "2026-10-18T00:00:00Z",
  stream: "stdout" as const,
  level: "Information" as const,
  text: "masked ***",
}
const page = {
  totalLines: 1000000,
  droppedLines: 0,
  truncated: false,
  captureInterrupted: false,
  truncationMarker: null,
  records: [{ line: 101, frame }],
  matches: [],
  nextCursor: null,
}

describe("attempt log ranges and bounded live tails", () => {
  beforeEach(() => vi.resetAllMocks())
  it("uses the exact attempt and bounded original line range with abort", async () => {
    get.mockResolvedValue({ response: new Response(), data: page })
    const signal = new AbortController().signal
    expect(await getTaskLogs(runId, attempt, 100, 400, signal)).toEqual({
      state: "ready",
      page,
    })
    expect(get).toHaveBeenCalledWith(
      "/api/v1/runs/{id}/tasks/{task}/{attempt}/logs",
      {
        params: {
          path: { id: runId, task: attempt.taskKey, attempt: "2" },
          query: { offset: "100", length: "400" },
        },
        signal,
      }
    )
  })
  it.each(["log_not_ready", "log_not_available"])(
    "reports explicit uploading state for %s only",
    async (reason) => {
      get.mockResolvedValue({
        response: new Response(null, { status: 409 }),
        error: { reason },
      })
      expect(await getTaskLogs(runId, attempt, 0, 400)).toEqual({
        state: "uploading",
      })
    }
  )
  it.each([401, 403, 404, 409, 502])(
    "does not convert HTTP %i to empty log success",
    async (status) => {
      get.mockResolvedValue({
        response: new Response(null, { status }),
        error: { title: "Unavailable" },
        data: page,
      })
      await expect(getTaskLogs(runId, attempt, 0, 400)).rejects.toMatchObject({
        status,
      })
    }
  )
  it.each([
    { ...attempt, attempt: 0 },
    { ...attempt, attempt: 32768 },
    { ...attempt, taskKey: "node/task/extra" },
    { ...attempt, taskKey: " /task" },
  ])("rejects invalid attempt before the network", async (selected) => {
    await expect(getTaskLogs(runId, selected, 0, 400)).rejects.toThrow()
    expect(get).not.toHaveBeenCalled()
  })
  it("rejects out-of-range and reordered records", async () => {
    for (const records of [
      [{ line: 1001, frame }],
      [
        { line: 2, frame },
        { line: 1, frame },
      ],
    ]) {
      get.mockResolvedValue({
        response: new Response(),
        data: { ...page, records },
      })
      await expect(getTaskLogs(runId, attempt, 0, 400)).rejects.toThrow(
        "inconsistent"
      )
    }
  })
  it("deduplicates replay, reports sequence gaps and bounds UTF-16 memory", () => {
    const first = parseLogBatch({
      id,
      records: [{ ...frame, line: 1 }],
      gap: false,
    })
    const initial = appendLogTail(emptyLogTail, first)
    expect(appendLogTail(initial, first)).toBe(initial)
    expect(
      appendLogTail(initial, { ...first, records: [{ ...frame, line: 3 }] }).gap
    ).toBe(true)
    let tail = emptyLogTail
    for (let line = 1; line <= 100; line++)
      tail = appendLogTail(tail, {
        id,
        records: [{ ...frame, line, text: "x".repeat(65536) }],
        gap: false,
      })
    expect(tail.records).toHaveLength(8)
    expect(tail.lastLine).toBe(100)
    expect(tail.gap).toBe(false)
    expect(() =>
      parseLogBatch({
        ...first,
        records: [
          { ...frame, line: 2 },
          { ...frame, line: 4 },
        ],
      })
    ).toThrow()
  })
})
