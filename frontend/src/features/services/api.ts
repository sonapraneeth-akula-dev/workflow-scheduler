import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import { catalogWorkflowSchema } from "@/features/catalog/api"
import { instantMicros, instantSchema } from "@/features/runs/search"
import {
  trackedScheduleSchema,
  trackingSchema,
} from "@/features/schedules/tracking"
import {
  maximumServiceRunLimit,
  serviceRunLimitSchema,
} from "@/lib/ServiceHistoryPreferences"

export const serviceSearchSchema = z
  .object({
    type: z
      .string()
      .min(1)
      .regex(/^[^/\\\p{Cc}]+$/u)
      .optional(),
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
const servicePageSchema = z.object({
  team: z.string().min(1),
  type: z.string().nullable(),
  runLimit: serviceRunLimitSchema,
  items: z
    .array(
      z.object({
        workflow: catalogWorkflowSchema,
        runs: z
          .array(
            catalogWorkflowSchema.shape.latestRun.unwrap().extend({
              createdAt: instantSchema,
              startedAt: instantSchema.nullable(),
              endedAt: instantSchema.nullable(),
            })
          )
          .max(maximumServiceRunLimit),
        schedules: z.array(trackedScheduleSchema).optional(),
        historyCards: z
          .array(
            z.object({
              id: z.string().regex(/^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$/),
              name: z
                .string()
                .min(1)
                .max(200)
                .regex(/^(?![\s\S]*\p{Cc})\S(?:[\s\S]*\S)?$/u),
              seriesIds: trackingSchema.shape.seriesIds.unwrap().min(1),
            })
          )
          .max(32)
          .nullable(),
      })
    )
    .max(50),
  nextCursor: z.string().min(1).nullable(),
})
export type ServiceWorkflow = z.infer<typeof servicePageSchema>["items"][number]
export type ServiceCursor = { cursor: string; afterId: string } | null

function consistentRuns({ runs }: ServiceWorkflow, runLimit: number) {
  if (runs.length > runLimit) return false
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
    return true
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
  runLimit: number,
  cursor: ServiceCursor,
  signal?: AbortSignal
) {
  z.string()
    .min(1)
    .regex(/^[^/\\\p{Cc}]+$/u)
    .parse(team)
  const search = serviceSearchSchema.parse({ type })
  serviceRunLimitSchema.parse(runLimit)
  const result = await publicApi.GET("/api/v1/service-history", {
    params: {
      query: {
        team,
        type: search.type,
        runLimit,
        limit: 50,
        cursor: cursor?.cursor,
      },
    },
    signal,
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  const page = servicePageSchema.parse(result.data)
  if (
    page.team !== team ||
    page.type !== (type ?? null) ||
    page.runLimit !== runLimit ||
    page.nextCursor === cursor?.cursor ||
    (page.nextCursor !== null && page.items.length === 0) ||
    page.items.some((entry, index) => {
      const { workflow } = entry
      return (
        workflow.team !== team ||
        (type !== undefined && workflow.type !== type) ||
        workflow.id !== `${team}/${workflow.type}/${workflow.name}` ||
        (index > 0
          ? compareIds(workflow.id, page.items[index - 1]!.workflow.id) <= 0
          : cursor !== null && compareIds(workflow.id, cursor.afterId) <= 0) ||
        !consistentRuns(entry, runLimit) ||
        (entry.historyCards !== null &&
          (new Set(entry.historyCards.map((card) => card.id)).size !==
            entry.historyCards.length ||
            entry.historyCards.some((card) =>
              card.seriesIds.some(
                (id) =>
                  !entry.schedules?.some((schedule) => schedule.seriesId === id)
              )
            )))
      )
    })
  )
    throw new Error(
      "The API returned inconsistent or out-of-scope service history."
    )
  return page
}
