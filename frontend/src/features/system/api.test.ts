import { beforeEach, describe, expect, it, vi } from "vitest"
import { getSystem, systemFrame, type SystemSnapshot } from "./api"
import { observation } from "./testFixture"

const { get } = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { GET: get },
}))

function respond(
  snapshot: unknown = observation.snapshot,
  id: string | null = observation.respondingInstanceId
) {
  get.mockResolvedValue({
    response: new Response(null, {
      headers: id ? { "X-Kardia-Instance": id } : {},
    }),
    data: JSON.stringify(snapshot),
  })
}

describe("system inventory boundary and observed activity", () => {
  beforeEach(() => vi.resetAllMocks())
  it("uses the typed admin endpoint, cancellation, precision-safe parsing and the actual responding process", async () => {
    respond()
    const signal = new AbortController().signal
    expect(await getSystem(signal)).toEqual(observation)
    expect(get).toHaveBeenCalledWith("/api/v1/admin/system", {
      signal,
      parseAs: "text",
    })
  })
  it("does not invent the serving instance when its header is absent", async () => {
    respond(observation.snapshot, null)
    expect((await getSystem()).respondingInstanceId).toBeNull()
    respond(observation.snapshot, "not-an-instance")
    await expect(getSystem()).rejects.toThrow()
  })
  it.each([
    { observedAt: "yesterday" },
    { heartbeatTimeoutSeconds: 0 },
    { totals: { ...observation.snapshot.totals, assignedTasks: -1 } },
    { totals: { ...observation.snapshot.totals, runningTasks: 4 } },
    {
      instances: [
        ...observation.snapshot.instances,
        observation.snapshot.instances[0],
      ],
    },
    {
      workers: [
        ...observation.snapshot.workers,
        observation.snapshot.workers[0],
      ],
    },
  ])("rejects inconsistent snapshots %j", async (change) => {
    respond({ ...observation.snapshot, ...change })
    await expect(getSystem()).rejects.toThrow()
  })
  it.each([
    { httpRequests: 9007199254740992 },
    { status: "Unknown" },
    { isLeader: true },
    { nodeName: "" },
  ])("rejects malformed process inventory %j", async (change) => {
    const snapshot = structuredClone(observation.snapshot)
    Object.assign(snapshot.instances[0], change)
    respond(snapshot)
    await expect(getSystem()).rejects.toThrow()
  })
  it.each([
    { id: "not-an-agent" },
    { runningTasks: 4 },
    { activeDagNodes: 4 },
    { activeRuns: 4 },
    { activeWorkflows: 2 },
    { slotsUsed: 5 },
  ])("rejects malformed worker inventory %j", async (change) => {
    const snapshot = structuredClone(observation.snapshot)
    Object.assign(snapshot.workers[0], change)
    respond(snapshot)
    await expect(getSystem()).rejects.toThrow()
  })
  it("rejects two active leaders and a stale controller claiming leadership", async () => {
    const snapshot = structuredClone(observation.snapshot)
    Object.assign(snapshot.instances[0], {
      role: "ApiController",
      isLeader: true,
    })
    respond(snapshot)
    await expect(getSystem()).rejects.toThrow("multiple active leaders")
    snapshot.instances[0].isLeader = false
    snapshot.instances[1].status = "Unhealthy"
    respond(snapshot)
    await expect(getSystem()).rejects.toThrow("healthy controller")
  })
  it("surfaces API outages and missing bodies instead of substituting an empty system", async () => {
    get.mockResolvedValue({
      response: new Response(null, { status: 503 }),
      error: { title: "System inventory unavailable" },
    })
    await expect(getSystem()).rejects.toThrow("System inventory unavailable")
    get.mockResolvedValue({ response: new Response(), data: undefined })
    await expect(getSystem()).rejects.toThrow("empty system inventory")
    respond({ ...observation.snapshot, workers: [], instances: [] })
    expect((await getSystem()).snapshot.workers).toEqual([])
  })
  it("does not fabricate activity on first observation, unchanged counters or new process IDs", () => {
    expect(systemFrame(observation).dispatchWorkers).toEqual([])
    expect(systemFrame(observation).heartbeatWorkers).toEqual([])
    expect(systemFrame(observation, observation).messageInstances).toEqual([])
    const restarted = structuredClone(observation)
    restarted.snapshot.instances[0].id = "33333333333333333333333333333333"
    restarted.snapshot.instances[0].liveDeliveries = 999
    expect(systemFrame(restarted, observation).messageInstances).toEqual([])
  })
  it("derives pulses only from increased assignments, newer heartbeats and successful send counters", () => {
    const changed = structuredClone(observation)
    changed.snapshot.workers[0].assignedTasks++
    changed.snapshot.workers[0].lastHeartbeatAt = "2026-10-21T08:00:01Z"
    changed.snapshot.instances[0].liveDeliveries++
    const frame = systemFrame(changed, observation)
    expect(frame.dispatchWorkers).toEqual([changed.snapshot.workers[0].id])
    expect(frame.heartbeatWorkers).toEqual([changed.snapshot.workers[0].id])
    expect(frame.messageInstances).toEqual([changed.snapshot.instances[0].id])
    const lower = structuredClone(observation)
    lower.snapshot.workers[0].assignedTasks = 0
    lower.snapshot.instances[0].liveDeliveries = 0
    expect(systemFrame(lower, observation).dispatchWorkers).toEqual([])
    expect(systemFrame(lower, observation).messageInstances).toEqual([])
  })
  it("retains the generated public snapshot shape", () => {
    const snapshot: SystemSnapshot = observation.snapshot
    expect(snapshot.workers).toMatchObject([{ activeDagNodes: 2 }])
  })
})
