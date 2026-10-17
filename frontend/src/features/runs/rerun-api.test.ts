import { beforeEach, describe, expect, it, vi } from "vitest"
import { rerunRun } from "./rerun-api"

const { post } = vi.hoisted(() => ({ post: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { POST: post },
}))
const parent = "019ed132-2f44-7000-8000-000000000001"
const runId = "019ed132-2f44-7000-8000-000000000002"
const key = "10101010-1111-4111-8111-111111111111"
describe("typed rerun boundary", () => {
  beforeEach(() => vi.resetAllMocks())
  it("sends only source identity and stable idempotency key; accepts only a new queued receipt", async () => {
    post.mockResolvedValue({
      response: new Response(null, { status: 202 }),
      data: { runId, variables: "private" },
    })
    expect(await rerunRun(parent, key)).toEqual({ runId })
    expect(post).toHaveBeenCalledWith("/api/v1/runs/{id}/rerun", {
      params: { path: { id: parent } },
      body: {},
      headers: { "Idempotency-Key": key },
    })
  })
  it.each([
    "",
    "../runs/other",
    parent.toUpperCase(),
    key,
    "00000000-0000-0000-0000-000000000000",
  ])("rejects noncanonical source %s before HTTP", async (source) => {
    await expect(rerunRun(source, key)).rejects.toThrow()
    expect(post).not.toHaveBeenCalled()
  })
  it("rejects invalid request keys before HTTP", async () => {
    await expect(rerunRun(parent, "not-a-key")).rejects.toThrow()
    expect(post).not.toHaveBeenCalled()
  })
  it.each([
    parent,
    key,
    null,
    "https://foreign.invalid",
    "019ed132-2f44-7000-8000-00000000000A",
  ])("rejects invalid or self receipts %s", async (id) => {
    post.mockResolvedValue({
      response: new Response(null, { status: 202 }),
      data: { runId: id },
    })
    await expect(rerunRun(parent, key)).rejects.toThrow()
  })
  it.each([200, 201])(
    "rejects success-shaped HTTP %i without acceptance",
    async (status) => {
      post.mockResolvedValue({
        response: new Response(null, { status }),
        data: { runId },
      })
      await expect(rerunRun(parent, key)).rejects.toThrow("HTTP 202")
    }
  )
  it.each([400, 401, 403, 409, 500, 503])(
    "preserves HTTP %i and never retries automatically",
    async (status) => {
      post.mockResolvedValue({
        response: new Response(null, { status }),
        error: { title: "Rerun unavailable" },
      })
      await expect(rerunRun(parent, key)).rejects.toMatchObject({
        status,
        message: "Rerun unavailable",
      })
      expect(post).toHaveBeenCalledTimes(1)
    }
  )
})
