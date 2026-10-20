import { beforeEach, expect, it, vi } from "vitest"
import { jsonObjectSchema, parseJson } from "@/lib/json"
import { cloneRun, getCloneSource } from "./clone-api"

const { get, post } = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { GET: get, POST: post },
}))
const expected = {
  runId: "019ed132-2f44-7000-8000-000000000001",
  workflowId: "team/regression/linear",
  versionId: "019ed132-2f44-7000-8000-000000000002",
}
const runId = "019ed132-2f44-7000-8000-000000000003"
const key = "019ed132-2f44-7000-8000-000000000004"
beforeEach(() => vi.resetAllMocks())

it("loads exact source inputs with cancellation and preserves nested JSON and unsafe number tokens", async () => {
  const json = `{"runId":"${expected.runId}","workflowId":"${expected.workflowId}","versionId":"${expected.versionId}","placement":null,"variables":{"seed":9223372036854775807,"regions":["us","eu"],"secret":{"Name":"api/token"}}}`
  get.mockResolvedValue({ response: new Response(), data: json })
  const signal = new AbortController().signal
  const source = await getCloneSource(expected, signal)
  expect(JSON.stringify(source.variables)).toBe(
    '{"seed":9223372036854775807,"regions":["us","eu"],"secret":{"Name":"api/token"}}'
  )
  expect(get).toHaveBeenCalledWith("/api/v1/runs/{id}/clone", {
    params: { path: { id: expected.runId } },
    parseAs: "text",
    signal,
  })
})

it.each(["runId", "workflowId", "versionId"] as const)(
  "rejects mismatched source %s",
  async (field) => {
    get.mockResolvedValue({
      response: new Response(),
      data: JSON.stringify({
        ...expected,
        variables: {},
        placement: null,
        [field]: field === "workflowId" ? "other/regression/linear" : runId,
      }),
    })
    await expect(getCloneSource(expected)).rejects.toThrow(
      "different run or pinned version"
    )
  }
)

it("submits only edited variables with an idempotency key to the explicit fresh-clone operation", async () => {
  post.mockResolvedValue({
    response: new Response(null, { status: 202 }),
    data: { runId },
  })
  const request = {
    variables: jsonObjectSchema.parse(
      parseJson(
        '{"nested":{"flag":true,"limit":9223372036854775807},"list":["a","b"]}'
      )
    ),
  }
  expect(await cloneRun(expected.runId, request, key)).toEqual({ runId })
  expect(post).toHaveBeenCalledWith("/api/v1/runs/{id}/clone", {
    params: { path: { id: expected.runId } },
    body: request,
    headers: { "Idempotency-Key": key },
  })
  expect(JSON.stringify(post.mock.calls[0]![1].body)).toContain(
    "9223372036854775807"
  )
})

it.each([200, 204])(
  "refuses unacknowledged acceptance with HTTP %i",
  async (status) => {
    post.mockResolvedValue({
      response: new Response(null, { status }),
      data: { runId },
    })
    await expect(
      cloneRun(expected.runId, { variables: {} }, key)
    ).rejects.toThrow("HTTP 202")
  }
)

it("rejects source-ID receipts, oversized UTF-8 payloads and invalid local identities", async () => {
  post.mockResolvedValue({
    response: new Response(null, { status: 202 }),
    data: { runId: expected.runId },
  })
  await expect(
    cloneRun(expected.runId, { variables: {} }, key)
  ).rejects.toThrow("source run")
  post.mockClear()
  await expect(
    cloneRun(expected.runId, { variables: { value: "é".repeat(524_288) } }, key)
  ).rejects.toThrow("1 MiB")
  await expect(cloneRun("invalid", { variables: {} }, key)).rejects.toThrow()
  await expect(
    cloneRun(expected.runId, { variables: {} }, "invalid")
  ).rejects.toThrow()
  expect(post).not.toHaveBeenCalled()
})

it("surfaces denied reads and rejected edits instead of reporting empty inputs or acceptance", async () => {
  get.mockResolvedValue({
    response: new Response(null, { status: 403 }),
    error: { title: "Operator access revoked" },
  })
  await expect(getCloneSource(expected)).rejects.toThrow(
    "Operator access revoked"
  )
  post.mockResolvedValue({
    response: new Response(null, { status: 400 }),
    error: { title: "Clone variable validation failed" },
  })
  await expect(
    cloneRun(expected.runId, { variables: {} }, key)
  ).rejects.toThrow("Clone variable validation failed")
})
