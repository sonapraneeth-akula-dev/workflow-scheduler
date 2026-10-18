import { beforeEach, describe, expect, it, vi } from "vitest"
import { getTaskOutputs } from "./outputs-api"

const { get } = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { GET: get },
}))
const runId = "019ed132-2f44-7000-8000-000000000001"
const attempt = { taskKey: "work/task", attempt: 2 }
const result = { runId, ...attempt, outputs: { value: 7, empty: null } }
describe("exact task outputs API", () => {
  beforeEach(() => vi.resetAllMocks())
  it("uses exact run/task/attempt, abort and no-store; preserves nested null", async () => {
    get.mockResolvedValue({
      response: new Response(),
      data: JSON.stringify(result),
    })
    const signal = new AbortController().signal
    expect(await getTaskOutputs(runId, attempt, signal)).toEqual(result)
    expect(get).toHaveBeenCalledWith("/api/v1/runs/{id}/outputs", {
      params: { path: { id: runId }, query: attempt },
      signal,
      cache: "no-store",
      parseAs: "text",
    })
  })
  it.each([null, {}])(
    "retains null versus empty outputs %j",
    async (outputs) => {
      get.mockResolvedValue({
        response: new Response(),
        data: JSON.stringify({ ...result, outputs }),
      })
      expect((await getTaskOutputs(runId, attempt)).outputs).toEqual(outputs)
    }
  )
  it.each([
    { runId: "019ed132-2f44-7000-8000-000000000002" },
    { taskKey: "other/task" },
    { attempt: 1 },
    { outputs: [] },
    { outputs: "text" },
    { outputs: false },
  ])("rejects malformed or cross-attempt response %j", async (patch) => {
    get.mockResolvedValue({
      response: new Response(),
      data: JSON.stringify({ ...result, ...patch }),
    })
    await expect(getTaskOutputs(runId, attempt)).rejects.toThrow()
  })
  it.each([
    ["019ED132-2F44-7000-8000-000000000001", attempt],
    [runId, { ...attempt, attempt: 0 }],
    [runId, { ...attempt, attempt: 32768 }],
    [runId, { ...attempt, taskKey: "node/task/more" }],
    [runId, { ...attempt, taskKey: " /task" }],
    [runId, { ...attempt, taskKey: `${"é".repeat(257)}/task` }],
  ])("rejects invalid identity before network", async (id, selected) => {
    await expect(getTaskOutputs(id, selected)).rejects.toThrow()
    expect(get).not.toHaveBeenCalled()
  })
  it.each([401, 403, 404, 503])(
    "retains HTTP %i without a data fallback",
    async (status) => {
      get.mockResolvedValue({
        response: new Response(null, { status }),
        error: { title: "Unavailable" },
        data: result,
      })
      await expect(getTaskOutputs(runId, attempt)).rejects.toMatchObject({
        status,
      })
    }
  )
  it("keeps large integer output text exact", async () => {
    get.mockResolvedValue({
      response: new Response(),
      data: JSON.stringify(result).replace(
        '"value":7',
        '"value":9223372036854775807'
      ),
    })
    expect(
      JSON.stringify((await getTaskOutputs(runId, attempt)).outputs)
    ).toContain("9223372036854775807")
  })
})
