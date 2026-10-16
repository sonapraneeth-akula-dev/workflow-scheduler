import { beforeEach, describe, expect, it, vi } from "vitest"
import { parseJson } from "@/lib/json"
import {
  getResourcePage,
  getResourceSummary,
  resourcePageSchema,
  resourceSummarySchema,
} from "./resource-api"

const { get } = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { GET: get },
}))
const runId = "019ed132-2f44-7000-8000-000000000001"
const summary = {
  taskKey: "work/task",
  attempt: 1,
  agentId: null,
  sampleCount: 2,
  averageCpuPercent: 125,
  maxCpuPercent: 150,
  peakRamBytes: 18446744073709551615n,
  networkSentBytes: 9007199254740993n,
  networkReceivedBytes: 0,
  cpuObservedSeconds: 1,
  networkObservedSeconds: 1,
  cpuUnavailableReason: null,
  memoryUnavailableReason: null,
  networkUnavailableReason: null,
}
function json(value: unknown): string {
  return JSON.stringify(value, (_, item: unknown) =>
    typeof item === "bigint" ? JSON.rawJSON(String(item)) : item
  )
}
const point = {
  atUtc: "2026-10-16T12:00:00Z",
  intervalSeconds: 1,
  cpuPercent: 125,
  ramBytes: 18446744073709551615n,
  networkSentBytes: 9007199254740993n,
  networkReceivedBytes: 0,
  networkSentDeltaBytes: 10,
  networkReceivedDeltaBytes: 0,
  networkSentBytesPerSecond: 10,
  networkReceivedBytesPerSecond: 0,
  cpuScope: "job-with-descendants",
  memoryScope: "job-resident-sum",
  networkScope: "host-process-etw-transport",
  cpuUnavailableReason: null,
  memoryUnavailableReason: null,
  networkUnavailableReason: null,
  networkRateUnavailableReason: null,
}
const sample = (sequence: number) => ({
  sequence,
  observation: {
    task: point,
    machine: null,
    machineUnavailableReason: "Machine sample not yet available.",
  },
})
const selected = { taskKey: "work/task", attempt: 1 }
describe("exact resource API", () => {
  beforeEach(() => vi.resetAllMocks())
  it("retains UInt64, decimal summary totals and null reasons without unsafe numeric coercion", async () => {
    get.mockResolvedValue({ response: new Response(), data: json([summary]) })
    const result = await getResourceSummary(runId)
    expect(result[0]!.peakRamBytes).toBe("18446744073709551615")
    expect(result[0]!.networkSentBytes).toBe("9007199254740993")
    expect(result[0]!.networkReceivedBytes).toBe("0")
    expect(result[0]!.averageCpuPercent).toBe(125)
    expect(get.mock.calls[0]![1].parseAs).toBe("text")
  })
  it("reads bounded, newest-first pages with exact path/query/cursor and abort signal", async () => {
    get.mockResolvedValue({
      response: new Response(),
      data: json({ samples: [sample(60), sample(59)], nextCursor: "eA.eQ" }),
    })
    const signal = new AbortController().signal
    const result = await getResourcePage(
      runId,
      selected,
      { token: "eQ.eA", before: "61" },
      signal
    )
    expect(result.samples[0]!.observation.task.ramBytes).toBe(
      "18446744073709551615"
    )
    expect(get).toHaveBeenCalledWith("/api/v1/runs/{id}/resources", {
      params: {
        path: { id: runId },
        query: { taskKey: "work/task", attempt: 1, limit: 60, cursor: "eQ.eA" },
      },
      signal,
      parseAs: "text",
    })
  })
  it.each([
    { samples: [sample(2), sample(2)], nextCursor: null },
    { samples: [sample(2), sample(3)], nextCursor: null },
    { samples: [sample(3), sample(1)], nextCursor: null },
    { samples: [], nextCursor: "eA.eQ" },
    {
      samples: Array.from({ length: 61 }, (_, index) => sample(61 - index)),
      nextCursor: null,
    },
    { samples: [sample(0)], nextCursor: null },
  ])(
    "rejects malformed, duplicate, gapped, oversized or unordered pages",
    (page) => {
      expect(resourcePageSchema.safeParse(parseJson(json(page))).success).toBe(
        false
      )
    }
  )
  it.each([
    { cpuPercent: null, cpuUnavailableReason: null },
    { cpuPercent: 0, cpuUnavailableReason: "Missing" },
    { cpuPercent: -1 },
    { cpuScope: "machine" },
    { ramBytes: 18446744073709551616n },
    { ramBytes: 1.5 },
    { networkSentBytesPerSecond: null },
    { networkSentDeltaBytes: null },
  ])(
    "rejects invalid values or misleading availability/scope metadata (case %#)",
    (override) => {
      const value = {
        ...sample(1),
        observation: {
          ...sample(1).observation,
          task: { ...point, ...override },
        },
      }
      expect(
        resourcePageSchema.safeParse(
          parseJson(json({ samples: [value], nextCursor: null }))
        ).success
      ).toBe(false)
    }
  )
  it("does not sort by corrected wall clock and distinguishes real zero from missing first-baseline rates", () => {
    const zero = {
      ...point,
      cpuPercent: 0,
      ramBytes: 0,
      networkSentBytes: 0,
      networkSentDeltaBytes: 0,
      networkSentBytesPerSecond: 0,
    }
    const first = {
      ...point,
      intervalSeconds: null,
      cpuPercent: null,
      cpuUnavailableReason: "First CPU baseline.",
      networkSentDeltaBytes: null,
      networkReceivedDeltaBytes: null,
      networkSentBytesPerSecond: null,
      networkReceivedBytesPerSecond: null,
      networkRateUnavailableReason: "First network baseline.",
    }
    const data = resourcePageSchema.parse(
      parseJson(
        json({
          samples: [
            {
              sequence: 2,
              observation: { ...sample(2).observation, task: zero },
            },
            {
              sequence: 1,
              observation: {
                ...sample(1).observation,
                task: { ...first, atUtc: "2026-10-16T13:00:00Z" },
              },
            },
          ],
          nextCursor: null,
        })
      )
    )
    expect(data.samples.map((value) => value.sequence)).toEqual(["2", "1"])
    expect(data.samples[0]!.observation.task.cpuPercent).toBe(0)
    expect(data.samples[1]!.observation.task.cpuPercent).toBeNull()
  })
  it("rejects duplicate attempt summaries and exponent-shaped integer tokens cleanly", () => {
    expect(
      resourceSummarySchema.safeParse(parseJson(json([summary, summary])))
        .success
    ).toBe(false)
    expect(
      resourceSummarySchema.safeParse(
        parseJson(json([{ ...summary, sampleCount: JSON.rawJSON("1e30") }]))
      ).success
    ).toBe(false)
  })
  it("rejects cross-page sequence jumps instead of mixing another window", async () => {
    get.mockResolvedValue({
      response: new Response(),
      data: json({ samples: [sample(62)], nextCursor: null }),
    })
    await expect(
      getResourcePage(runId, selected, { token: "eA.eQ", before: "61" })
    ).rejects.toThrow("outside the requested page")
  })
  it.each([400, 403, 404, 503])(
    "surfaces HTTP %i without partial-data fallbacks or retries",
    async (status) => {
      get.mockResolvedValue({
        response: new Response(null, { status }),
        error: { title: "Resource read failed." },
      })
      await expect(getResourcePage(runId, selected, null)).rejects.toThrow(
        "Resource read failed."
      )
      expect(get).toHaveBeenCalledTimes(1)
    }
  )
  it("validates identity, attempt, task scope and cursor before network access", async () => {
    await expect(getResourceSummary("bad")).rejects.toThrow()
    await expect(
      getResourcePage(runId, { ...selected, attempt: 0 }, null)
    ).rejects.toThrow()
    await expect(
      getResourcePage(runId, { ...selected, taskKey: "wrong" }, null)
    ).rejects.toThrow()
    await expect(
      getResourcePage(runId, selected, { token: "bad", before: "1" })
    ).rejects.toThrow()
    expect(get).not.toHaveBeenCalled()
  })
})
