import { beforeEach, describe, expect, it, vi } from "vitest"
import { getRunConditions, type RunConditions } from "./conditions-api"

const { get } = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { GET: get },
}))
const runId = "019ed132-2f44-7000-8000-000000000001"
const versionId = "019ed132-2f44-7000-8000-000000000002"
const details: RunConditions["conditions"][number] = {
  entityKey: "test",
  variableKey: "runFinalTest",
  nodeKey: null,
  outputKey: null,
  expected: true,
  actual: false,
  outcome: "NotMatched",
  evaluatedAt: "2026-10-20T03:00:00Z",
  evaluatedSequence: "32",
  sourceKind: "RunVariable",
  declaredDefault: true,
  sourceRunId: runId,
  producerTaskKey: null,
  producerAttempt: null,
  producedAt: null,
}
const result: RunConditions = {
  runId,
  versionId,
  lastSeq: "9223372036854775807",
  conditions: [details],
}
describe("condition debug API", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    get.mockResolvedValue({ response: new Response(), data: result })
  })
  it("uses a public no-store abortable read, pinned version and exact Int64 strings", async () => {
    const signal = new AbortController().signal
    expect(await getRunConditions(runId, versionId, signal)).toEqual(result)
    expect(get).toHaveBeenCalledWith("/api/v1/runs/{id}/conditions", {
      params: { path: { id: runId } },
      signal,
      cache: "no-store",
    })
  })
  it.each([
    { runId: versionId },
    { versionId: runId },
    { lastSeq: 32 },
    { lastSeq: "01" },
    { lastSeq: "9223372036854775808" },
    { conditions: [details, details] },
    { variables: { secret: "must not display" } },
    { conditions: [{ ...details, actual: "secret" }] },
    { conditions: [{ ...details, actual: true }] },
    { conditions: [{ ...details, sourceKind: "Secret" }] },
    { conditions: [{ ...details, outcome: null }] },
    { conditions: [{ ...details, producedAt: details.evaluatedAt }] },
  ])(
    "rejects wrong identities, unsafe or inconsistent evidence %j",
    async (patch) => {
      get.mockResolvedValue({
        response: new Response(),
        data: { ...result, ...patch },
      })
      await expect(getRunConditions(runId, versionId)).rejects.toThrow()
    }
  )
  it.each([401, 403, 500, 503])(
    "preserves HTTP %i without stale data fallback",
    async (status) => {
      get.mockResolvedValue({
        response: new Response(null, { status }),
        error: { title: "Evidence unavailable" },
        data: result,
      })
      await expect(getRunConditions(runId, versionId)).rejects.toMatchObject({
        status,
      })
    }
  )
  it("rejects invalid run identity before requesting data", async () => {
    await expect(getRunConditions("invalid", versionId)).rejects.toThrow()
    expect(get).not.toHaveBeenCalled()
  })
  it("retains an invalid value's producer without accepting or exposing the value", async () => {
    const invalid = {
      ...details,
      variableKey: null,
      nodeKey: "dataset",
      outputKey: "cacheMissing",
      sourceKind: "NodeOutput",
      declaredDefault: null,
      actual: null,
      outcome: "InvalidType",
      producerTaskKey: "dataset/check-cache",
      producerAttempt: 1,
      producedAt: details.evaluatedAt,
    }
    get.mockResolvedValue({
      response: new Response(),
      data: { ...result, conditions: [invalid] },
    })
    expect((await getRunConditions(runId, versionId)).conditions).toEqual([
      invalid,
    ])
  })
})
