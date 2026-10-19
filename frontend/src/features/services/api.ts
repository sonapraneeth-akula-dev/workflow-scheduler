import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import { catalogWorkflowSchema, runStatuses } from "@/features/catalog/api"
import { instantMicros, instantSchema } from "@/features/runs/search"

export const serviceSearchSchema = z
  .object({
    type: z
      .string()
      .min(1)
      .regex(/^[^/\\\p{Cc}]+$/u)
      .optional(),
    window: z
      .enum(["7d", "14d", "30d", "90d"])
      .default("14d")
      .transform(() => "14d" as const),
    view: z.enum(["cards", "table"]).default("cards"),
    groupBy: z.enum(["type", "metadata"]).default("type"),
    groupKey: z.string().min(1).optional(),
  })
  .superRefine((search, context) => {
    if (search.groupBy === "metadata" && !search.groupKey)
      context.addIssue({
        code: "custom",
        path: ["groupKey"],
        message: "Choose a metadata key to group containers by.",
      })
  })
export type ServiceSearch = z.infer<typeof serviceSearchSchema>
export const healthDaySchema = z.object({
  day: z.iso.date(),
  counts: z.partialRecord(z.enum(runStatuses), z.int().positive()),
})
const servicePageSchema = z.object({
  team: z.string().min(1),
  type: z.string().nullable(),
  window: z.literal("14d"),
  from: z.iso.date(),
  to: z.iso.date(),
  items: z
    .array(
      z.object({
        workflow: catalogWorkflowSchema,
        days: z.array(healthDaySchema).max(14),
        runs: z
          .array(
            catalogWorkflowSchema.shape.latestRun.unwrap().extend({
              createdAt: instantSchema,
              startedAt: instantSchema.nullable(),
              endedAt: instantSchema.nullable(),
            })
          )
          .max(50),
      })
    )
    .max(50),
  nextCursor: z.string().min(1).nullable(),
})
export type HealthDay = z.infer<typeof healthDaySchema>
export type ServiceWorkflow = z.infer<typeof servicePageSchema>["items"][number]
export type ServiceCursor = { cursor: string; afterId: string } | null

function consistentRuns({ days, runs }: ServiceWorkflow) {
  const total = days.reduce(
    (sum, day) =>
      sum +
      Object.values(day.counts).reduce((count, value) => count + value, 0),
    0
  )
  if (!Number.isSafeInteger(total) || runs.length !== Math.min(total, 50))
    return false
  const remaining = new Map(
    days.flatMap(({ day, counts }) =>
      Object.entries(counts).map(([status, count]) => [
        `${day}/${status}`,
        count,
      ])
    )
  )
  const ids = new Set<string>()
  return runs.every((run, index) => {
    const id = run.id.toLowerCase()
    const previous = runs[index - 1]
    const at = instantMicros(run.createdAt)
    if (
      ids.has(id) ||
      (previous &&
        (at > instantMicros(previous.createdAt) ||
          (at === instantMicros(previous.createdAt) &&
            id >= previous.id.toLowerCase())))
    )
      return false
    ids.add(id)
    const key = `${new Date(run.createdAt).toISOString().slice(0, 10)}/${run.status}`
    const count = remaining.get(key) ?? 0
    remaining.set(key, count - 1)
    return count > 0
  })
}

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
  window: ServiceSearch["window"],
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
    page.items.some((entry, index) => {
      const { workflow, days: health } = entry
      return (
        workflow.team !== team ||
        (type !== undefined && workflow.type !== type) ||
        workflow.id !== `${team}/${workflow.type}/${workflow.name}` ||
        (index > 0
          ? compareIds(workflow.id, page.items[index - 1]!.workflow.id) <= 0
          : cursor !== null && compareIds(workflow.id, cursor.afterId) <= 0) ||
        !consistentRuns(entry) ||
        health.length !== days ||
        health.some(
          (day, index) =>
            day.day !==
            new Date(start + index * 86400000).toISOString().slice(0, 10)
        )
      )
    })
  )
    throw new Error(
      "The API returned inconsistent or out-of-scope service history."
    )
  return page
}
