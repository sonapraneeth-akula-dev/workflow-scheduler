import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import type { components } from "@/lib/api/schema"

export async function runNow(scheduleId: string, key: string) {
  const id = z.uuid().parse(scheduleId)
  const idempotencyKey = z.uuid().parse(key)
  const result = await publicApi.POST("/api/v1/schedules/{id}/run-now", {
    params: { path: { id } },
    headers: { "Idempotency-Key": idempotencyKey },
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  if (result.response.status !== 202)
    throw new Error("The API did not acknowledge run acceptance with HTTP 202.")
  return z
    .object({ runId: z.uuid() })
    .parse(
      result.data
    ) satisfies components["schemas"]["ScheduleRunNowResponse"]
}

export const trendWindowSchema = z.enum(["7d", "30d", "90d"])
export type TrendWindow = z.infer<typeof trendWindowSchema>
const count = z.number().int().nonnegative().max(Number.MAX_SAFE_INTEGER)
const measurement = z.number().finite().nonnegative().nullable()
const metricsSchema = z
  .strictObject({
    runs: count,
    succeeded: count,
    failed: count,
    durationCount: count,
    successRate: z.number().min(0).max(1).nullable(),
    p50Ms: measurement,
    p95Ms: measurement,
  })
  .refine(
    (value) =>
      value.succeeded + value.failed === value.runs &&
      value.durationCount <= value.runs &&
      (value.runs === 0
        ? value.successRate === null
        : value.successRate !== null &&
          Math.abs(value.successRate - value.succeeded / value.runs) < 1e-12) &&
      (value.durationCount === 0
        ? value.p50Ms === null && value.p95Ms === null
        : value.p50Ms !== null &&
          value.p95Ms !== null &&
          value.p95Ms >= value.p50Ms),
    "Trend counts, rates and observed duration quantiles must agree."
  )
export const seriesTrendsSchema = z
  .strictObject({
    seriesId: z.uuid(),
    window: trendWindowSchema,
    from: z.iso.date(),
    to: z.iso.date(),
    totals: metricsSchema,
    streak: z.strictObject({
      outcome: z.enum(["Succeeded", "Failed"]).nullable(),
      runs: count,
    }),
    days: z
      .array(z.strictObject({ day: z.iso.date(), metrics: metricsSchema }))
      .max(90),
  })
  .refine((value) => {
    const days = Number(value.window.slice(0, -1))
    const start = Date.parse(value.from)
    const streak = value.streak
    return (
      Date.parse(value.to) - start === days * 86400000 &&
      value.days.length === days &&
      value.days.every(
        (day, index) =>
          day.day ===
          new Date(start + index * 86400000).toISOString().slice(0, 10)
      ) &&
      (["runs", "succeeded", "failed", "durationCount"] as const).every(
        (key) =>
          value.days.reduce(
            (sum, day) => sum + BigInt(day.metrics[key]),
            0n
          ) === BigInt(value.totals[key])
      ) &&
      (value.totals.runs === 0
        ? streak.outcome === null && streak.runs === 0
        : streak.outcome !== null &&
          streak.runs > 0 &&
          streak.runs <=
            (streak.outcome === "Succeeded"
              ? value.totals.succeeded
              : value.totals.failed))
    )
  }, "Trend days, totals and streak must agree with the selected window.")
export type SeriesTrends = z.infer<typeof seriesTrendsSchema>

export async function getTrends(
  seriesId: string,
  window: TrendWindow,
  signal?: AbortSignal
) {
  const id = z
    .uuid()
    .refine((value) => value !== "00000000-0000-0000-0000-000000000000")
    .parse(seriesId)
  const period = trendWindowSchema.parse(window)
  const result = await publicApi.GET("/api/v1/series/{id}/trends", {
    params: { path: { id }, query: { window: period } },
    signal,
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  const response = seriesTrendsSchema.parse(result.data)
  if (response.seriesId !== id || response.window !== period)
    throw new Error("The API returned trends for a different series or window.")
  return response satisfies components["schemas"]["SeriesTrendsResponse"]
}
