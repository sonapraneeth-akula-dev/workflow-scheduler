import { beforeEach, describe, expect, it, vi } from "vitest"
import { parseJson } from "@/lib/json"
import {
  getTriggerSchema,
  maximumTriggerBytes,
  triggerRequestBytes,
  triggerRun,
} from "./trigger-api"

const { get, post } = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { GET: get, POST: post },
}))
const workflowId = "team/type/workflow"
const versionId = "019ed132-2f44-7000-8000-000000000001"
const key = "11111111-1111-4111-8111-111111111111"
const runId = "019ed132-2f44-7000-8000-000000000002"
const source = `{"workflowId":"${workflowId}","versionId":"${versionId}","version":"1.0.0","variables":[{"key":"large","type":"Long","required":true,"default":9223372036854775807,"allowedValues":[]}]}`
describe("typed workflow trigger API", () => {
  beforeEach(() => vi.resetAllMocks())
  it("reads the exact workflow and optional historical UUID using lossless response parsing", async () => {
    get.mockResolvedValue({ response: new Response(), data: source })
    const result = await getTriggerSchema(workflowId, versionId)
    expect(JSON.stringify(result.variables[0]!.default)).toBe(
      "9223372036854775807"
    )
    expect(get).toHaveBeenCalledWith(
      "/api/v1/workflows/{team}/{type}/{workflow}/variables",
      {
        params: {
          path: { team: "team", type: "type", workflow: "workflow" },
          query: { version: versionId },
        },
        signal: undefined,
        parseAs: "text",
      }
    )
  })
  it.each([
    source.replace(workflowId, "other/type/workflow"),
    source.replace(versionId, runId),
    "{}",
  ])("rejects foreign, mismatched or malformed schemas", async (data) => {
    get.mockResolvedValue({ response: new Response(), data })
    await expect(getTriggerSchema(workflowId, versionId)).rejects.toThrow()
  })
  it.each([400, 403, 404, 503])(
    "surfaces HTTP %i without fallback defaults or a retry",
    async (status) => {
      get.mockResolvedValue({
        response: new Response(null, { status }),
        error: { title: "Schema unavailable." },
      })
      await expect(getTriggerSchema(workflowId)).rejects.toThrow(
        "Schema unavailable."
      )
      expect(get).toHaveBeenCalledTimes(1)
    }
  )
  it("requires HTTP 202 and preserves exact typed values with an idempotency key", async () => {
    post.mockResolvedValue({
      response: new Response(null, { status: 202 }),
      data: { runId },
    })
    const request = {
      workflowId,
      version: versionId,
      variables: { large: parseJson("9223372036854775807") },
    }
    await triggerRun(
      { ...request, variables: { large: JSON.rawJSON("9223372036854775807") } },
      key
    )
    expect(JSON.stringify(post.mock.calls[0]![1].body)).toBe(
      `{"workflowId":"${workflowId}","version":"${versionId}","variables":{"large":9223372036854775807}}`
    )
    expect(post.mock.calls[0]![1].headers).toEqual({ "Idempotency-Key": key })
    post.mockResolvedValue({ response: new Response(), data: { runId } })
    await expect(
      triggerRun({ workflowId, version: versionId, variables: {} }, key)
    ).rejects.toThrow("HTTP 202")
  })
  it("checks request size, version UUID and request key before networking", async () => {
    await expect(
      triggerRun(
        {
          workflowId,
          version: versionId,
          variables: { huge: "x".repeat(1_048_576) },
        },
        key
      )
    ).rejects.toThrow("1 MiB")
    await expect(
      triggerRun({ workflowId, version: "bad", variables: {} }, key)
    ).rejects.toThrow()
    await expect(
      triggerRun({ workflowId, version: versionId, variables: {} }, "bad")
    ).rejects.toThrow()
    expect(post).not.toHaveBeenCalled()
  })
  it("accepts exactly 1 MiB of UTF-8 request bytes and rejects the next multibyte character", async () => {
    post.mockResolvedValue({
      response: new Response(null, { status: 202 }),
      data: { runId },
    })
    const request = { workflowId, version: versionId, variables: { value: "" } }
    request.variables.value = "x".repeat(
      maximumTriggerBytes - triggerRequestBytes(request)
    )
    expect(triggerRequestBytes(request)).toBe(maximumTriggerBytes)
    await expect(triggerRun(request, key)).resolves.toEqual({ runId })
    request.variables.value += "\u2603"
    expect(triggerRequestBytes(request)).toBe(maximumTriggerBytes + 3)
    await expect(triggerRun(request, key)).rejects.toThrow("1 MiB")
    expect(post).toHaveBeenCalledTimes(1)
  })
  it.each([{}, { runId: "bad" }])(
    "rejects malformed acceptance receipts",
    async (data) => {
      post.mockResolvedValue({
        response: new Response(null, { status: 202 }),
        data,
      })
      await expect(
        triggerRun({ workflowId, version: versionId, variables: {} }, key)
      ).rejects.toThrow()
    }
  )
})
