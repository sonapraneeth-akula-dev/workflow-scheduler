import { beforeEach, describe, expect, it, vi } from "vitest"
import { getTrends, runNow, type SeriesTrends, type TrendWindow } from "./api"

const { post, get } = vi.hoisted(() => ({ post: vi.fn(), get: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { POST: post, GET: get },
}))
const scheduleId = "019ed132-2f44-7000-8000-000000000001"
const key = "11111111-1111-4111-8111-111111111111"
const runId = "019ed132-2f44-7000-8000-000000000002"
describe("Series API", () => {
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

  function trends(): SeriesTrends {
    const metrics = {
      runs: 1,
      succeeded: 1,
      failed: 0,
      durationCount: 1,
      successRate: 1,
      p50Ms: 10,
      p95Ms: 10,
    }
    return {
      seriesId: scheduleId,
      window: "7d",
      from: "2026-10-11",
      to: "2026-10-18",
      totals: { ...metrics, runs: 7, succeeded: 7, durationCount: 7 },
      streak: { outcome: "Succeeded", runs: 7 },
      days: Array.from({ length: 7 }, (_, index) => ({
        day: `2026-10-${index + 11}`,
        metrics: { ...metrics },
      })),
    }
  }
  describe("validated series trend API", () => {
    it("uses the public generated operation, exact window and abort signal", async () => {
      const data = trends()
      get.mockResolvedValue({ response: new Response(), data })
      const signal = new AbortController().signal
      expect(await getTrends(scheduleId, "7d", signal)).toEqual(data)
      expect(get).toHaveBeenCalledWith("/api/v1/series/{id}/trends", {
        params: { path: { id: scheduleId }, query: { window: "7d" } },
        signal,
      })
    })
    it.each([
      ["invalid", "7d"],
      ["00000000-0000-0000-0000-000000000000", "7d"],
      [scheduleId, "7d,90d"],
    ])("rejects invalid identity/window before HTTP", async (id, period) => {
      await expect(getTrends(id!, period as TrendWindow)).rejects.toThrow()
      expect(get).not.toHaveBeenCalled()
    })
    it.each([400, 401, 403, 500, 503])(
      "surfaces HTTP %i without retrying or returning cached/empty measurements",
      async (status) => {
        get.mockResolvedValue({
          response: new Response(null, { status }),
          error: { title: "Trends unavailable." },
        })
        await expect(getTrends(scheduleId, "7d")).rejects.toThrow(
          "Trends unavailable."
        )
        expect(get).toHaveBeenCalledTimes(1)
      }
    )
    it.each([
      "foreign-series",
      "foreign-window",
      "missing-day",
      "duplicate-day",
      "reordered-day",
      "invalid-day",
      "wrong-range",
      "wrong-counts",
      "wrong-rate",
      "unsafe-count",
      "unknown-total-measurement",
      "invented-total-measurement",
      "reversed-quantiles",
      "nonfinite-quantile",
      "negative-count",
      "wrong-day-sum",
      "empty-streak",
      "unbounded-streak",
      "private-content",
    ])("rejects success-shaped corrupt data: %s", async (corruption) => {
      const data = trends()
      switch (corruption) {
        case "foreign-series":
          data.seriesId = runId
          break
        case "foreign-window":
          data.window = "30d"
          break
        case "missing-day":
          data.days.pop()
          break
        case "duplicate-day":
          data.days[1]!.day = data.days[0]!.day
          break
        case "reordered-day":
          data.days.reverse()
          break
        case "invalid-day":
          data.days[0]!.day = "2026-02-30"
          break
        case "wrong-range":
          data.to = "2026-10-19"
          break
        case "wrong-counts":
          data.totals.failed = 1
          break
        case "wrong-rate":
          data.totals.successRate = 0.5
          break
        case "unsafe-count":
          data.totals.runs = Number.MAX_SAFE_INTEGER + 1
          break
        case "unknown-total-measurement":
          data.totals.p50Ms = null
          break
        case "invented-total-measurement":
          data.totals.durationCount = 0
          break
        case "reversed-quantiles":
          data.totals.p95Ms = 5
          break
        case "nonfinite-quantile":
          data.totals.p95Ms = Infinity
          break
        case "negative-count":
          data.days[0]!.metrics.runs = -1
          break
        case "wrong-day-sum":
          data.days[0]!.metrics.durationCount = 0
          data.days[0]!.metrics.p50Ms = null
          data.days[0]!.metrics.p95Ms = null
          break
        case "empty-streak":
          data.streak = { outcome: null, runs: 0 }
          break
        case "unbounded-streak":
          data.streak.runs = 8
          break
        case "private-content":
          Object.assign(data, { variables: { secret: "private" } })
          break
      }
      get.mockResolvedValue({ response: new Response(), data })
      await expect(getTrends(scheduleId, "7d")).rejects.toThrow()
    })
    it("keeps truly empty days and unobserved metrics null instead of fabricating rates or durations", async () => {
      const data = trends()
      const metrics = {
        runs: 0,
        succeeded: 0,
        failed: 0,
        durationCount: 0,
        successRate: null,
        p50Ms: null,
        p95Ms: null,
      }
      data.totals = metrics
      data.days = data.days.map((day) => ({ ...day, metrics }))
      data.streak = { outcome: null, runs: 0 }
      get.mockResolvedValue({ response: new Response(), data })
      expect((await getTrends(scheduleId, "7d")).totals).toEqual(metrics)
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
