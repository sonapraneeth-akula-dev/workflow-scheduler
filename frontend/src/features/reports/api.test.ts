import { beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import { getReportIndex, getReportView } from "./api"

const { call } = vi.hoisted(() => ({ call: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { GET: call },
}))
const runId = "019ed132-2f44-7000-8000-000000000001"
const reportId = "019ed132-2f44-7000-8000-000000000002"
const item = {
  id: reportId,
  runId,
  sourceKey: "$post/summary",
  attempt: 2,
  kind: "text",
  title: "Summary",
  tags: ["review", "review"],
  sizeBytes: 7,
  createdAt: "2026-10-16T08:00:00+00:00",
}
const success = (data: unknown) => ({ data, response: new Response(null) })
beforeEach(() => call.mockReset())

describe("trusted report metadata and content reads", () => {
  it("retains exact source, attempt, title and ordered duplicate tags", async () => {
    call.mockResolvedValue(success({ runId, items: [item] }))
    expect(await getReportIndex(runId)).toEqual({ runId, items: [item] })
  })
  it("supports an empty index", async () => {
    call.mockResolvedValue(success({ runId, items: [] }))
    expect((await getReportIndex(runId)).items).toEqual([])
  })
  it.each([
    { runId: reportId, items: [] },
    { runId, items: [{ ...item, runId: reportId }] },
    { runId, items: [item, item] },
  ])("rejects inconsistent index identities", async (data) => {
    call.mockResolvedValue(success(data))
    await expect(getReportIndex(runId)).rejects.toThrow(
      "The API returned an inconsistent report index."
    )
  })
  it.each([
    { ...item, sizeBytes: -1 },
    { ...item, sizeBytes: Number.MAX_SAFE_INTEGER + 1 },
    { ...item, attempt: 0 },
    { ...item, sourceKey: "not-a-task" },
    { ...item, sourceKey: "node/task/extra" },
    { ...item, kind: " " },
    { ...item, title: "\u03bb".repeat(257) },
    { ...item, tags: Array.from({ length: 33 }, () => "tag") },
    { ...item, createdAt: "invalid" },
  ])("rejects malformed index metadata", async (report) => {
    call.mockResolvedValue(success({ runId, items: [report] }))
    await expect(getReportIndex(runId)).rejects.toThrow()
  })
  it.each([
    "invalid",
    runId.toUpperCase(),
    "00000000-0000-4000-8000-000000000000",
  ])(
    "rejects noncanonical run identity before requesting anything",
    async (id) => {
      await expect(getReportIndex(id)).rejects.toThrow()
      expect(call).not.toHaveBeenCalled()
    }
  )
  it("fetches binary content only after matching committed run metadata", async () => {
    const content = new Blob(["payload"])
    call
      .mockResolvedValueOnce(success({ runId, items: [item] }))
      .mockResolvedValueOnce(success(content))
    const signal = new AbortController().signal
    const result = await getReportView(runId, reportId, signal)
    expect(result).toEqual({ report: item, content })
    expect(call).toHaveBeenNthCalledWith(1, "/api/v1/runs/{id}/reports", {
      params: { path: { id: runId } },
      signal,
    })
    expect(call).toHaveBeenNthCalledWith(2, "/api/v1/reports/{id}/content", {
      params: { path: { id: reportId } },
      parseAs: "blob",
      signal,
    })
  })
  it("never reads a report belonging to a different run or absent from the index", async () => {
    call.mockResolvedValue(success({ runId, items: [] }))
    await expect(getReportView(runId, reportId)).rejects.toThrow(
      "Report was not found in this run."
    )
    expect(call).toHaveBeenCalledOnce()
  })
  it.each([null, "payload", new Blob(["short"])])(
    "rejects missing, nonbinary or truncated content",
    async (content) => {
      call
        .mockResolvedValueOnce(success({ runId, items: [item] }))
        .mockResolvedValueOnce(success(content))
      await expect(getReportView(runId, reportId)).rejects.toThrow(
        "Report content does not match its committed byte size."
      )
    }
  )
  it.each([401, 403, 503])(
    "surfaces refused metadata HTTP %i",
    async (status) => {
      call.mockResolvedValue({
        error: { title: "Reports unavailable." },
        response: new Response(null, { status }),
      })
      await expect(getReportIndex(runId)).rejects.toBeInstanceOf(ApiError)
      expect(call).toHaveBeenCalledOnce()
    }
  )
  it("surfaces refused content without returning stale metadata as success", async () => {
    call
      .mockResolvedValueOnce(success({ runId, items: [item] }))
      .mockResolvedValueOnce({
        error: { title: "Content unavailable." },
        response: new Response(null, { status: 403 }),
      })
    await expect(getReportView(runId, reportId)).rejects.toThrow(
      "Content unavailable."
    )
  })
})
