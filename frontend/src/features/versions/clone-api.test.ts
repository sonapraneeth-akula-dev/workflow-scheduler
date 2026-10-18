import { Blob as NodeBlob } from "node:buffer"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import { cloneWorkflow } from "./api"

const { post } = vi.hoisted(() => ({ post: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { POST: post },
}))
const version = "019ed132-2f44-7000-8000-000000000001"
const request = { newName: "experiment", targetType: "other/release" }
const headers = {
  "Content-Type": "application/zip",
  "Content-Disposition": "attachment; filename=workflow-clone.zip",
  "Cache-Control": "no-store",
}
function respond(
  bytes = new Uint8Array([80, 75, 3, 4]),
  overrides: Record<string, string> = headers
) {
  const cancel = vi.fn()
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      controller.enqueue(bytes)
      controller.close()
    },
    cancel,
  })
  post.mockResolvedValue({
    response: new Response(null, { headers: overrides }),
    data: stream,
  })
  return { stream, cancel }
}
describe("clone download trust boundaries", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    vi.stubGlobal("Blob", NodeBlob)
  })
  afterEach(() => vi.unstubAllGlobals())
  it("pins exact history UUID, source path segments, strict body and a bounded ZIP download", async () => {
    respond()
    const blob = await cloneWorkflow("team/type/workflow", version, request)
    expect(blob.type).toBe("application/zip")
    expect(blob.size).toBe(4)
    expect(post).toHaveBeenCalledWith(
      "/api/v1/workflows/{team}/{type}/{workflow}/versions/{version}/clone",
      {
        params: {
          path: { team: "team", type: "type", workflow: "workflow", version },
        },
        body: request,
        parseAs: "stream",
        redirect: "error",
      }
    )
  })
  it.each([
    ["team/type/workflow", "latest", request],
    ["team/type/workflow", version.toUpperCase(), request],
    ["team/type/workflow", "00000000-0000-0000-0000-000000000000", request],
    ["team/type", version, request],
    ["team/../workflow", version, request],
    ["team/type/workflow", version, { ...request, newName: "../escape" }],
    ["team/type/workflow", version, { ...request, newName: " " }],
    [
      "team/type/workflow",
      version,
      { ...request, targetType: "one/two/three" },
    ],
    [
      "team/type/workflow",
      version,
      { ...request, newName: "workflow", targetType: "team/type" },
    ],
  ] as const)(
    "rejects invalid identity/version/body without contacting API (%s, %s)",
    async (identity, id, body) => {
      await expect(cloneWorkflow(identity, id, body)).rejects.toThrow()
      expect(post).not.toHaveBeenCalled()
    }
  )
  it.each([400, 401, 403, 404, 409, 503])(
    "preserves HTTP %i problem errors without treating them as downloads",
    async (status) => {
      post.mockResolvedValue({
        response: new Response(null, { status }),
        error: { title: "Clone unavailable", code: "clone_target_exists" },
      })
      await expect(
        cloneWorkflow("team/type/workflow", version, request)
      ).rejects.toMatchObject({
        status,
        message: "Clone unavailable",
      } satisfies Partial<ApiError>)
    }
  )
  it.each([
    { ...headers, "Content-Type": "text/html" },
    { ...headers, "Content-Disposition": "inline" },
    { ...headers, "Cache-Control": "public" },
    { ...headers, "Content-Length": String(2 * 1024 * 1024 + 1) },
    { ...headers, "Content-Length": "invalid" },
  ])(
    "rejects invalid download headers and cancels stream",
    async (overrides) => {
      const { cancel } = respond(undefined, overrides)
      await expect(
        cloneWorkflow("team/type/workflow", version, request)
      ).rejects.toThrow("ZIP attachment")
      expect(cancel).toHaveBeenCalled()
    }
  )
  it("rejects unknown-length oversized archives, empty and truncated bodies", async () => {
    respond(new Uint8Array(2 * 1024 * 1024 + 1))
    await expect(
      cloneWorkflow("team/type/workflow", version, request)
    ).rejects.toThrow("size limit")
    respond(new Uint8Array())
    await expect(
      cloneWorkflow("team/type/workflow", version, request)
    ).rejects.toThrow("incomplete")
    respond(undefined, { ...headers, "Content-Length": "100" })
    await expect(
      cloneWorkflow("team/type/workflow", version, request)
    ).rejects.toThrow("incomplete")
  })
  it("propagates network failure and a partial-stream failure, never creating a Blob", async () => {
    post.mockRejectedValueOnce(new Error("Network unavailable"))
    await expect(
      cloneWorkflow("team/type/workflow", version, request)
    ).rejects.toThrow("Network unavailable")
    const stream = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.error(new Error("Download interrupted"))
      },
    })
    post.mockResolvedValue({
      response: new Response(null, { headers }),
      data: stream,
    })
    await expect(
      cloneWorkflow("team/type/workflow", version, request)
    ).rejects.toThrow("Download interrupted")
    expect(stream.locked).toBe(false)
  })
})
