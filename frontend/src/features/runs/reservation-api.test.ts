import { beforeEach, describe, expect, it, vi } from "vitest"
import { getReservationQueue } from "./reservation-api"

const { get } = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { GET: get },
}))
const runId = "019ed132-2f44-7000-8000-000000000001"
const item = {
  nodeKey: "work",
  position: 2,
  count: 1,
  queuedAt: "2026-10-16T08:00:00Z",
  blockingReservationId: "019ed132-2f44-7000-8000-000000000002",
  blockingRunId: "019ed132-2f44-7000-8000-000000000003",
}
const result = { runId, items: [item] }
describe("reservation queue API", () => {
  beforeEach(() => vi.resetAllMocks())
  it("reads exact run without caching and propagates cancellation", async () => {
    get.mockResolvedValue({ response: new Response(), data: result })
    const signal = new AbortController().signal
    expect(await getReservationQueue(runId, signal)).toEqual(result)
    expect(get).toHaveBeenCalledWith("/api/v1/runs/{id}/reservation-queue", {
      params: { path: { id: runId } },
      signal,
      cache: "no-store",
    })
  })
  it.each([
    { ...result, items: [] },
    {
      ...result,
      items: [{ ...item, blockingReservationId: null, blockingRunId: null }],
    },
  ])("retains empty waits and redacted blockers", async (data) => {
    get.mockResolvedValue({ response: new Response(), data })
    expect(await getReservationQueue(runId)).toEqual(data)
  })
  it.each([
    { ...result, runId: item.blockingRunId },
    { ...result, items: [{ ...item, position: 0 }] },
    { ...result, items: [{ ...item, count: -1 }] },
    { ...result, items: [{ ...item, position: 1.5 }] },
    { ...result, items: [{ ...item, nodeKey: "node/task" }] },
    { ...result, items: [{ ...item, queuedAt: "yesterday" }] },
    { ...result, items: [{ ...item, blockingRunId: null }] },
    { ...result, items: [item, { ...item, position: 3 }] },
    { ...result, items: [item, { ...item, nodeKey: "next", position: 1 }] },
  ])("rejects malformed, unsorted or cross-run data", async (data) => {
    get.mockResolvedValue({ response: new Response(), data })
    await expect(getReservationQueue(runId)).rejects.toThrow()
  })
  it.each([401, 403, 500, 503])(
    "retains HTTP %i without stale success",
    async (status) => {
      get.mockResolvedValue({
        response: new Response(null, { status }),
        error: { title: "Queue unavailable" },
        data: result,
      })
      await expect(getReservationQueue(runId)).rejects.toMatchObject({ status })
    }
  )
  it.each([
    "bad",
    runId.toUpperCase(),
    item.blockingRunId.replace("-7000-", "-4000-"),
  ])("rejects invalid identity before network", async (id) => {
    await expect(getReservationQueue(id)).rejects.toThrow()
    expect(get).not.toHaveBeenCalled()
  })
})
