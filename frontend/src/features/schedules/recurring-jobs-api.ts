import { z } from "zod"
import { ApiError, publicApi, scheduleSchema } from "@/lib/api/client"
import { parseJson } from "@/lib/json"
import { runFilterSchema } from "@/features/runs/search"
import { durationSeconds } from "./schedule-draft"

export const recurringJobsSearchSchema = runFilterSchema
  .pick({ team: true, type: true })
  .superRefine((search, context) => {
    if (search.type && !search.team)
      context.addIssue({
        code: "custom",
        path: ["type"],
        message: "Choose a team before a workflow type.",
      })
  })
export type RecurringJobsSearch = z.infer<typeof recurringJobsSearchSchema>
export type ScheduleCursor = { cursor: string; afterId: string } | null

const recurringJobSchema = scheduleSchema
  .extend({
    workflowId: runFilterSchema.shape.workflowId.unwrap(),
    name: z.string().min(1),
    timezone: z.string().min(1),
    cron: z.string().min(1).nullable(),
  })
  .refine(
    (job) => (job.cron === null) !== (job.interval === null),
    "A recurring job must have exactly one cron or interval."
  )
const pageSchema = z.object({
  items: z.array(recurringJobSchema).max(50),
  nextCursor: z.string().min(1).nullable(),
})
export type RecurringJob = z.infer<typeof recurringJobSchema>

export async function getRecurringJobs(
  search: RecurringJobsSearch,
  cursor: ScheduleCursor,
  signal?: AbortSignal
) {
  const filters = recurringJobsSearchSchema.parse(search)
  const result = await publicApi.GET("/api/v1/schedules", {
    params: {
      query: { ...filters, limit: 50, cursor: cursor?.cursor },
    },
    parseAs: "text",
    signal,
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  if (typeof result.data !== "string")
    throw new Error("The API returned an empty recurring jobs response.")
  const page = pageSchema.parse(parseJson(result.data))
  if (
    page.nextCursor === cursor?.cursor ||
    (page.nextCursor !== null && page.items.length === 0) ||
    page.items.some((job, index) => {
      if (job.interval !== null) durationSeconds(job.interval)
      const [team, type] = job.workflowId.split("/")
      const previous = index ? page.items[index - 1]!.id : cursor?.afterId
      return (
        (filters.team !== undefined && team !== filters.team) ||
        (filters.type !== undefined && type !== filters.type) ||
        (previous !== undefined &&
          job.id.toLowerCase() <= previous.toLowerCase())
      )
    })
  )
    throw new Error(
      "The API returned inconsistent or out-of-scope recurring jobs."
    )
  return page
}
