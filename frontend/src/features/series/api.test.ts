import { beforeEach, describe, expect, it, vi } from "vitest"
import { runNow } from "./api"

const { post } = vi.hoisted(() => ({ post: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { POST: post },
}))
const scheduleId = "019ed132-2f44-7000-8000-000000000001"
const key = "11111111-1111-4111-8111-111111111111"
const runId = "019ed132-2f44-7000-8000-000000000002"
describe("Run now API", () => {
  beforeEach(() => vi.resetAllMocks())
  it("uses the actual schedule path and request key without variable/version/series overrides", async () => {
    post.mockResolvedValue({
      response: new Response(null, { status: 202 }),
      data: { runId },
    })
    expect(await runNow(scheduleId, key)).toEqual({ runId })
    expect(post).toHaveBeenCalledWith("/api/v1/schedules/{id}/run-now", {
      params: { path: { id: scheduleId } },
      headers: { "Idempotency-Key": key },
    })
  })
  it.each([
    ["invalid", key],
    [scheduleId, "invalid"],
  ])(
    "validates schedule and key before network access",
    async (id, requestKey) => {
      await expect(runNow(id!, requestKey!)).rejects.toThrow()
      expect(post).not.toHaveBeenCalled()
    }
  )
  it.each([{}, { runId: null }, { runId: "invalid" }])(
    "rejects malformed receipts: %j",
    async (data) => {
      post.mockResolvedValue({
        response: new Response(null, { status: 202 }),
        data,
      })
      await expect(runNow(scheduleId, key)).rejects.toThrow()
    }
  )
  it("rejects success-shaped nonacceptance responses", async () => {
    post.mockResolvedValue({ response: new Response(), data: { runId } })
    await expect(runNow(scheduleId, key)).rejects.toThrow("HTTP 202")
  })
  it.each([400, 403, 404, 409, 503])(
    "surfaces HTTP %i without retrying",
    async (status) => {
      post.mockResolvedValue({
        response: new Response(null, { status }),
        error: { title: "Run not accepted." },
      })
      await expect(runNow(scheduleId, key)).rejects.toThrow("Run not accepted.")
      expect(post).toHaveBeenCalledTimes(1)
    }
  )
})
