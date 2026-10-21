import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { getKubernetesMemory } from "./api"
import { accessToken } from "@/lib/auth/session"

vi.mock("@/lib/auth/session", () => ({ accessToken: vi.fn() }))
const request = vi.fn()
const node = {
  name: "kardia-lab-01",
  capacityBytes: 3221225472,
  allocatableBytes: 3000000000,
  workingSetBytes: 1073741824,
  sampledAt: "2026-10-21T15:00:00Z",
}
const snapshot = {
  observedAt: "2026-10-21T15:00:00Z",
  staleAfterSeconds: 60,
  nodes: [node],
  pods: [],
}
function respond(body: unknown, status = 200) {
  request.mockResolvedValue(new Response(JSON.stringify(body), { status }))
}
afterEach(() => vi.unstubAllGlobals())
beforeEach(() => {
  vi.resetAllMocks()
  vi.stubGlobal("fetch", request)
  vi.mocked(accessToken).mockResolvedValue("admin-token")
})
describe("Kubernetes RAM boundary", () => {
  it("uses the authenticated fixed admin route, no cache and caller cancellation", async () => {
    respond(snapshot)
    const signal = new AbortController().signal
    expect(await getKubernetesMemory(signal)).toEqual(snapshot)
    expect(request).toHaveBeenCalledWith(
      "/api/v1/admin/system/kubernetes-memory",
      {
        signal,
        cache: "no-store",
        headers: { Authorization: "Bearer admin-token" },
      }
    )
  })
  it.each([
    { workingSetBytes: -1 },
    { workingSetBytes: 9007199254740992 },
    { sampledAt: null },
    { sampledAt: "yesterday" },
    { workingSetBytes: null },
    { capacityBytes: 0 },
    { name: "" },
  ])(
    "rejects malformed measurements instead of manufacturing zero %j",
    async (change) => {
      respond({ ...snapshot, nodes: [{ ...node, ...change }] })
      await expect(getKubernetesMemory()).rejects.toThrow()
    }
  )
  it("retains actual missing samples and their absent sample time as null", async () => {
    const missing = {
      ...snapshot,
      nodes: [{ ...node, workingSetBytes: null, sampledAt: null }],
    }
    respond(missing)
    expect(await getKubernetesMemory()).toEqual(missing)
  })
  it("surfaces cluster permission and configuration failures", async () => {
    respond(
      {
        title: "Kubernetes RAM monitoring unavailable",
        detail: "Read-only Metrics API permission denied",
      },
      503
    )
    await expect(getKubernetesMemory()).rejects.toThrow(
      "Read-only Metrics API permission denied"
    )
  })
  it("does not send a fictional bearer token for the explicit local lab profile", async () => {
    vi.mocked(accessToken).mockResolvedValue(null)
    respond(snapshot)
    await getKubernetesMemory()
    expect(request.mock.calls[0]?.[1]).toMatchObject({ headers: undefined })
  })
})
