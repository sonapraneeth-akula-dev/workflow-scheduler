import { beforeEach, describe, expect, it, vi } from "vitest"
import {
  dashboardResourceSchema,
  encodeDashboardId,
  getDashboard,
  type Dashboard,
  type DashboardScope,
} from "./api"

const { get } = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { GET: get },
}))
const resource = "team/type/workflow"
const id = encodeDashboardId(resource)
const uuid = "019ed132-2f44-7000-8000-000000000001"
function fixture(): Dashboard {
  const row = {
    workflowId: resource,
    nodeKey: "alpha",
    observations: 4,
    failures: 1,
    softFailures: 1,
    durationCount: 2,
    failureRate: 0.25,
    p50Ms: 10,
    p95Ms: 20,
    cpuMaxPercent: 150,
    ramPeakMb: 512,
    latestRunId: uuid,
    runHref: `/runs/${uuid}`,
  }
  return {
    scope: "workflow",
    id,
    window: "7d",
    from: "2026-10-11",
    to: "2026-10-18",
    top: 10,
    observationKind: "Node",
    rankings: { slowest: [row], failureRate: [row], cpu: [row], ram: [row] },
  }
}
describe("validated scoped dashboard API", () => {
  beforeEach(() => vi.resetAllMocks())
  it("uses the generated public operation and exact filters, preserving cancellation", async () => {
    const data = fixture()
    const signal = new AbortController().signal
    get.mockResolvedValue({ response: new Response(), data })
    expect(await getDashboard("workflow", id, "7d", 10, signal)).toEqual(data)
    expect(get).toHaveBeenCalledWith("/api/v1/dashboards/{scope}/{id}", {
      params: {
        path: { scope: "workflow", id },
        query: { window: "7d", top: 10 },
      },
      signal,
    })
  })
  it.each([
    ["workflow", id],
    ["node", encodeDashboardId(`${resource}/\u03b2`)],
    ["workflow", encodeDashboardId(`\u03b1/type/workflow`)],
    ["schedule", uuid],
    ["machine", uuid],
  ])("accepts canonical %s identities including Unicode", (scope, id) => {
    expect(dashboardResourceSchema.safeParse({ scope, id }).success).toBe(true)
  })
  it.each([
    ["foreign", id],
    ["workflow", `${id}=`],
    ["workflow", "%%%"],
    ["workflow", encodeDashboardId("team/type")],
    ["workflow", encodeDashboardId("team/../wf")],
    ["workflow", encodeDashboardId(`${"a".repeat(129)}/type/wf`)],
    ["workflow", encodeDashboardId(" /type/wf")],
    ["node", id],
    ["node", encodeDashboardId(`${resource}/a/b`)],
    ["node", encodeDashboardId(`${resource}/..`)],
    ["schedule", "00000000-0000-0000-0000-000000000000"],
    ["machine", uuid.toUpperCase()],
  ])("rejects invalid resource %s %s before HTTP", async (scope, id) => {
    await expect(
      getDashboard(scope as DashboardScope, id!, "7d")
    ).rejects.toThrow()
    expect(get).not.toHaveBeenCalled()
  })
  it.each([0, 51, 1.5, Infinity])(
    "rejects invalid top %s before HTTP",
    async (top) => {
      await expect(getDashboard("workflow", id, "7d", top)).rejects.toThrow()
      expect(get).not.toHaveBeenCalled()
    }
  )
  it.each([400, 401, 403, 500, 503])(
    "surfaces HTTP %i without success-shaped defaults or retries",
    async (status) => {
      get.mockResolvedValue({
        response: new Response(null, { status }),
        error: { title: "Dashboard unavailable." },
      })
      await expect(getDashboard("workflow", id, "7d")).rejects.toThrow(
        "Dashboard unavailable."
      )
      expect(get).toHaveBeenCalledTimes(1)
    }
  )
  it.each([
    "scope",
    "id",
    "window",
    "top",
    "range",
    "kind",
    "count",
    "rate",
    "unsafe",
    "unmeasured",
    "quantiles",
    "negative",
    "infinite",
    "link",
    "workflow",
    "duplicate",
    "null-ranking",
    "cross-list",
    "private",
  ])("rejects inconsistent success data: %s", async (mutation) => {
    const data = fixture()
    const row = data.rankings.slowest[0]!
    switch (mutation) {
      case "scope":
        data.scope = "machine"
        break
      case "id":
        data.id = "foreign"
        break
      case "window":
        data.window = "30d"
        break
      case "top":
        data.top = 5
        break
      case "range":
        data.to = "2026-10-19"
        break
      case "kind":
        data.observationKind = "TaskAttempt"
        break
      case "count":
        row.softFailures = 4
        break
      case "rate":
        row.failureRate = 0.5
        break
      case "unsafe":
        row.observations = Number.MAX_SAFE_INTEGER + 1
        break
      case "unmeasured":
        row.durationCount = 0
        break
      case "quantiles":
        row.p95Ms = 5
        break
      case "negative":
        row.ramPeakMb = -1
        break
      case "infinite":
        row.cpuMaxPercent = Infinity
        break
      case "link":
        row.runHref = "https://foreign.invalid/run"
        break
      case "workflow":
        row.workflowId = "foreign/type/workflow"
        break
      case "duplicate":
        data.rankings.slowest.push({ ...row })
        break
      case "null-ranking":
        row.cpuMaxPercent = null
        break
      case "cross-list":
        data.rankings.cpu[0] = { ...row, observations: 8, failures: 2 }
        break
      case "private":
        Object.assign(row, { variables: { secret: "private" } })
        break
    }
    get.mockResolvedValue({ response: new Response(), data })
    await expect(getDashboard("workflow", id, "7d")).rejects.toThrow()
  })
  it("rejects reversed rankings and nonordinal tie order", async () => {
    const data = fixture()
    const row = data.rankings.slowest[0]!
    data.rankings.slowest.push({ ...row, nodeKey: "beta", p95Ms: 30 })
    get.mockResolvedValue({ response: new Response(), data })
    await expect(getDashboard("workflow", id, "7d")).rejects.toThrow()
    data.rankings.slowest = [{ ...row, nodeKey: "beta" }, row]
    await expect(getDashboard("workflow", id, "7d")).rejects.toThrow()
  })
  it("retains empty rankings, zero measurements and unknown measurements without invention", async () => {
    const data = fixture()
    const row = data.rankings.slowest[0]!
    Object.assign(row, {
      failures: 0,
      softFailures: 0,
      failureRate: 0,
      p50Ms: 0,
      p95Ms: 0,
      cpuMaxPercent: 0,
      ramPeakMb: 0,
    })
    get.mockResolvedValue({ response: new Response(), data })
    expect(
      (await getDashboard("workflow", id, "7d")).rankings.slowest[0]!.p95Ms
    ).toBe(0)
    data.rankings = {
      slowest: [],
      failureRate: [
        {
          ...row,
          durationCount: 0,
          p50Ms: null,
          p95Ms: null,
          cpuMaxPercent: null,
          ramPeakMb: null,
        },
      ],
      cpu: [],
      ram: [],
    }
    expect(
      (await getDashboard("workflow", id, "7d")).rankings.failureRate[0]!.p95Ms
    ).toBeNull()
    data.rankings.failureRate = []
    expect(
      (await getDashboard("workflow", id, "7d")).rankings.failureRate
    ).toEqual([])
  })
  it("compares ordinal workflow and node identities separately for cross-workflow machine ties", async () => {
    const data = fixture()
    data.scope = "machine"
    data.id = uuid
    data.observationKind = "TaskAttempt"
    const rows = ["team/type/a", "team/type/a-"].map((workflowId) => ({
      ...data.rankings.slowest[0]!,
      workflowId,
    }))
    data.rankings = { slowest: rows, failureRate: rows, cpu: rows, ram: rows }
    get.mockResolvedValue({ response: new Response(), data })
    expect(
      (await getDashboard("machine", uuid, "7d")).rankings.slowest.map(
        (row) => row.workflowId
      )
    ).toEqual(["team/type/a", "team/type/a-"])
    rows.reverse()
    await expect(getDashboard("machine", uuid, "7d")).rejects.toThrow()
  })
  it("rejects success-shaped empty observations accompanied by measured rankings", async () => {
    const data = fixture()
    data.rankings.failureRate = []
    get.mockResolvedValue({ response: new Response(), data })
    await expect(getDashboard("workflow", id, "7d")).rejects.toThrow()
  })
})
