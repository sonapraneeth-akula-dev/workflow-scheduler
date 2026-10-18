import { beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import { getReportComparison, getReportIndex, getReportView } from "./api"

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

  describe("exact cross-run report comparisons", () => {
    const seriesId = "019ed132-2f44-7000-8000-000000000003"
    const rightRunId = "019ed132-2f44-7000-8000-000000000004"
    const query = {
      leftRunId: runId,
      rightRunId,
      sourceKey: "$post/summary",
      title: " Summary \u03bb ",
    }
    const left = { ...item, title: query.title }
    const right = {
      ...left,
      id: "019ed132-2f44-7000-8000-000000000005",
      runId: rightRunId,
      kind: "markdown",
      attempt: 3,
    }
    const pair = { seriesId, left, right }

    it("validates both exact identities then loads both Blobs without re-reading indexes", async () => {
      const content = new Blob(["payload"])
      call
        .mockResolvedValueOnce(success(pair))
        .mockResolvedValueOnce(success(content))
        .mockResolvedValueOnce(success(content))
      const signal = new AbortController().signal
      expect(await getReportComparison(seriesId, query, signal)).toEqual({
        left: { report: left, content },
        right: { report: right, content },
      })
      expect(call).toHaveBeenCalledTimes(3)
      expect(call).toHaveBeenNthCalledWith(
        1,
        "/api/v1/series/{id}/reports/compare",
        { params: { path: { id: seriesId }, query }, signal }
      )
      for (const [index, report] of [left, right].entries())
        expect(call).toHaveBeenNthCalledWith(
          index + 2,
          "/api/v1/reports/{id}/content",
          {
            params: { path: { id: report.id } },
            parseAs: "blob",
            signal,
          }
        )
    })
    it.each([
      { ...pair, seriesId: rightRunId },
      { ...pair, left: right, right: left },
      { ...pair, left: { ...left, sourceKey: "$post/Summary" } },
      { ...pair, right: { ...right, title: query.title.trim() } },
      { ...pair, right: { ...right, id: left.id } },
    ])(
      "rejects mismatched metadata before fetching either payload",
      async (data) => {
        call.mockResolvedValueOnce(success(data))
        await expect(getReportComparison(seriesId, query)).rejects.toThrow(
          "The API returned an inconsistent report comparison."
        )
        expect(call).toHaveBeenCalledOnce()
      }
    )
    it.each([
      { ...pair, right: null },
      { ...pair, left: { ...left, attempt: 0 } },
      { ...pair, right: { ...right, sizeBytes: -1 } },
    ])("rejects invalid response metadata before content", async (data) => {
      call.mockResolvedValueOnce(success(data))
      await expect(getReportComparison(seriesId, query)).rejects.toThrow()
      expect(call).toHaveBeenCalledOnce()
    })
    it.each([
      { ...query, rightRunId: runId },
      { ...query, leftRunId: runId.toUpperCase() },
      { ...query, sourceKey: "not-a-task" },
      { ...query, sourceKey: `node/${"x".repeat(1021)}` },
      { ...query, title: "\u03bb".repeat(257) },
      { ...query, title: " " },
    ])(
      "rejects invalid selections before network access",
      async (selection) => {
        await expect(getReportComparison(seriesId, selection)).rejects.toThrow()
        expect(call).not.toHaveBeenCalled()
      }
    )
    it.each([
      seriesId.toUpperCase(),
      "00000000-0000-0000-0000-000000000000",
      "invalid",
    ])("rejects noncanonical series IDs", async (id) => {
      await expect(getReportComparison(id, query)).rejects.toThrow()
      expect(call).not.toHaveBeenCalled()
    })
    it.each([401, 403, 404, 409, 503])(
      "surfaces HTTP %i without reading content or manufacturing an empty side",
      async (status) => {
        call.mockResolvedValueOnce({
          response: new Response(null, { status }),
          error: { title: "Comparison unavailable." },
        })
        await expect(getReportComparison(seriesId, query)).rejects.toThrow(
          "Comparison unavailable."
        )
        expect(call).toHaveBeenCalledOnce()
      }
    )
    it("fails the comparison if either content is truncated", async () => {
      call
        .mockResolvedValueOnce(success(pair))
        .mockResolvedValueOnce(success(new Blob(["payload"])))
        .mockResolvedValueOnce(success(new Blob(["short"])))
      await expect(getReportComparison(seriesId, query)).rejects.toThrow(
        "Report content does not match its committed byte size."
      )
    })
    it("fails the comparison if a content authorization is revoked", async () => {
      call
        .mockResolvedValueOnce(success(pair))
        .mockResolvedValueOnce(success(new Blob(["payload"])))
        .mockResolvedValueOnce({
          response: new Response(null, { status: 403 }),
          error: { title: "Content refused." },
        })
      await expect(getReportComparison(seriesId, query)).rejects.toThrow(
        "Content refused."
      )
    })
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
