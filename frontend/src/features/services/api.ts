import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import { catalogWorkflowSchema, runStatuses } from "@/features/catalog/api"
import { trendWindowSchema, type TrendWindow } from "@/features/series/api"

export const serviceSearchSchema = z.object({
  type: z
    .string()
    .min(1)
    .regex(/^[^/\\\p{Cc}]+$/u)
    .optional(),
  window: trendWindowSchema.default("30d"),
  view: z.enum(["cards", "table"]).default("cards"),
})
export type ServiceSearch = z.infer<typeof serviceSearchSchema>
export const healthDaySchema = z.object({
  day: z.iso.date(),
  counts: z.partialRecord(z.enum(runStatuses), z.int().positive()),
})
const servicePageSchema = z.object({
  team: z.string().min(1),
  type: z.string().nullable(),
  window: trendWindowSchema,
  from: z.iso.date(),
  to: z.iso.date(),
  items: z
    .array(
      z.object({
        workflow: catalogWorkflowSchema,
        days: z.array(healthDaySchema).max(90),
      })
    )
    .max(50),
  nextCursor: z.string().min(1).nullable(),
})
export type HealthDay = z.infer<typeof healthDaySchema>
export type ServiceWorkflow = z.infer<typeof servicePageSchema>["items"][number]
export type ServiceCursor = { cursor: string; afterId: string } | null

function compareIds(left: string, right: string) {
  const a = Array.from(left, (char) => char.codePointAt(0)!)
  const b = Array.from(right, (char) => char.codePointAt(0)!)
  for (let index = 0; index < Math.max(a.length, b.length); index++) {
    const difference = (a[index] ?? -1) - (b[index] ?? -1)
    if (difference) return difference
  }
  return 0
}

export async function getServiceHistory(
  team: string,
  type: string | undefined,
  window: TrendWindow,
  cursor: ServiceCursor,
  signal?: AbortSignal
) {
  z.string()
    .min(1)
    .regex(/^[^/\\\p{Cc}]+$/u)
    .parse(team)
  const search = serviceSearchSchema.parse({ type, window })
  const result = await publicApi.GET("/api/v1/service-history", {
    params: {
      query: {
        team,
        type: search.type,
        window: search.window,
        limit: 50,
        cursor: cursor?.cursor,
      },
    },
    signal,
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  const page = servicePageSchema.parse(result.data)
  const days = Number(window.slice(0, -1))
  const start = Date.parse(page.from)
  if (
    page.team !== team ||
    page.type !== (type ?? null) ||
    page.window !== window ||
    Date.parse(page.to) - start !== days * 86400000 ||
    page.nextCursor === cursor?.cursor ||
    (page.nextCursor !== null && page.items.length === 0) ||
    page.items.some(
      ({ workflow, days: health }, index) =>
        workflow.team !== team ||
        (type !== undefined && workflow.type !== type) ||
        workflow.id !== `${team}/${workflow.type}/${workflow.name}` ||
        (index > 0
          ? compareIds(workflow.id, page.items[index - 1]!.workflow.id) <= 0
          : cursor !== null && compareIds(workflow.id, cursor.afterId) <= 0) ||
        !Number.isSafeInteger(
          health.reduce(
            (sum, day) =>
              sum +
              Object.values(day.counts).reduce(
                (count, value) => count + value,
                0
              ),
            0
          )
        ) ||
        health.length !== days ||
        health.some(
          (day, index) =>
            day.day !==
            new Date(start + index * 86400000).toISOString().slice(0, 10)
        )
    )
  )
    throw new Error(
      "The API returned inconsistent or out-of-scope service history."
    )
  return page
}

export function healthSummary(counts: HealthDay["counts"]) {
  const total = Object.values(counts).reduce((sum, value) => sum + value, 0)
  if (!total) return "No runs"
  return runStatuses
    .filter((status) => counts[status])
    .map((status) => `${counts[status]} ${status.toLowerCase()}`)
    .join(", ")
}

export function healthState(counts: HealthDay["counts"]) {
  if (counts.Failed || counts.TimedOut) return "Failed"
  if (counts.Aborted) return "Aborted"
  if (counts.Queued || counts.Running || counts.Aborting) return "Running"
  return counts.Succeeded ? "Succeeded" : "NoRuns"
}
