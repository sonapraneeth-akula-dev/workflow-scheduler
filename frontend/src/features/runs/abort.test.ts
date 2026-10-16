import { beforeEach, describe, expect, it, vi } from "vitest"
import { abortReasonSchema, abortRun } from "./abort"

const { post } = vi.hoisted(() => ({ post: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { POST: post },
}))
const runId = "019ed132-2f44-7000-8000-000000000001"
const receipt = {
  by: "11111111-1111-1111-1111-111111111111",
  byName: "Original operator",
  at: "2026-10-16T08:00:00.1234567Z",
  reason: "Original reason",
  source: "API",
}
describe("browser abort trust boundaries", () => {
  beforeEach(() => vi.resetAllMocks())
  it.each(["", " ", "\r\n\t", "x".repeat(501)])(
    "rejects invalid reason %j before sending",
    async (reason) => {
      await expect(abortRun(runId, reason)).rejects.toThrow()
      expect(post).not.toHaveBeenCalled()
    }
  )
  it("preserves exact reason/source and the immutable first-wins receipt", async () => {
    const reason = "  keep spacing\nand lines  "
    post.mockResolvedValue({
      response: new Response(null, { status: 202 }),
      data: receipt,
    })
    expect(await abortRun(runId, reason)).toEqual(receipt)
    expect(post).toHaveBeenCalledWith("/api/v1/runs/{id}/abort", {
      params: { path: { id: runId } },
      body: { reason, source: "UI" },
    })
    expect(abortReasonSchema.safeParse("x".repeat(500)).success).toBe(true)
  })
  it("validates identity before network access", async () => {
    await expect(abortRun("invalid", "Reason")).rejects.toThrow()
    expect(post).not.toHaveBeenCalled()
  })
  it.each([400, 403, 409, 503])(
    "surfaces HTTP %i with no automatic retry or invented terminal state",
    async (status) => {
      post.mockResolvedValue({
        response: new Response(null, { status }),
        error: { title: "Abort rejected." },
      })
      await expect(abortRun(runId, "Reason")).rejects.toThrow("Abort rejected.")
      expect(post).toHaveBeenCalledTimes(1)
    }
  )
  it.each([
    { ...receipt, reason: "" },
    { ...receipt, source: "unknown" },
    { ...receipt, at: "invalid" },
    {},
  ])("rejects malformed receipt %j", async (data) => {
    post.mockResolvedValue({
      response: new Response(null, { status: 202 }),
      data,
    })
    await expect(abortRun(runId, "Reason")).rejects.toThrow()
  })
  it("rejects success-shaped nonacceptance", async () => {
    post.mockResolvedValue({ response: new Response(), data: receipt })
    await expect(abortRun(runId, "Reason")).rejects.toThrow("HTTP 202")
  })
})
