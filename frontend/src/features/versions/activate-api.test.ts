import { beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import { activateVersion } from "./api"

const { post } = vi.hoisted(() => ({ post: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { POST: post },
}))
const workflowId = "team/type/workflow"
const target = "019ed132-2f44-7000-8000-000000000001"
const latest = "019ed132-2f44-7000-8000-000000000002"
const result = { workflowId, activeVersionId: target, latestVersionId: latest }

describe("version activation trust boundary", () => {
  beforeEach(() => vi.resetAllMocks())
  it.each([latest, null])(
    "posts only an exact target path with no body, retaining latest %s",
    async (latestVersionId) => {
      post.mockResolvedValue({
        response: new Response(),
        data: { ...result, latestVersionId },
      })
      expect(await activateVersion(workflowId, target)).toEqual({
        ...result,
        latestVersionId,
      })
      expect(post).toHaveBeenCalledExactlyOnceWith(
        "/api/v1/workflows/{team}/{type}/{workflow}/versions/{version}/activate",
        {
          params: {
            path: {
              team: "team",
              type: "type",
              workflow: "workflow",
              version: target,
            },
          },
          redirect: "error",
        }
      )
    }
  )
  it.each([
    ["team/type", target],
    ["team/../workflow", target],
    ["team/type/work\\flow", target],
    [workflowId, "latest"],
    [workflowId, target.toUpperCase()],
    [workflowId, "00000000-0000-0000-0000-000000000000"],
  ])(
    "rejects malformed identity %s @ %s before sending",
    async (identity, version) => {
      await expect(activateVersion(identity, version)).rejects.toThrow()
      expect(post).not.toHaveBeenCalled()
    }
  )
  it.each([
    undefined,
    {},
    { ...result, workflowId: "another/type/workflow" },
    { ...result, activeVersionId: latest },
    { ...result, latestVersionId: "latest" },
    { ...result, latestVersionId: undefined },
  ])(
    "rejects missing, mismatched or malformed activation responses",
    async (data) => {
      post.mockResolvedValue({ response: new Response(), data })
      await expect(activateVersion(workflowId, target)).rejects.toThrow()
    }
  )
  it.each([403, 404, 409, 500, 503])(
    "preserves problem details for HTTP %s",
    async (status) => {
      post.mockResolvedValue({
        response: new Response(null, { status }),
        error: {
          title:
            "Existing schedules are incompatible with this workflow version",
        },
      })
      await expect(activateVersion(workflowId, target)).rejects.toMatchObject({
        status,
        message: expect.stringContaining("Existing schedules are incompatible"),
      })
      await expect(activateVersion(workflowId, target)).rejects.toBeInstanceOf(
        ApiError
      )
    }
  )
})
