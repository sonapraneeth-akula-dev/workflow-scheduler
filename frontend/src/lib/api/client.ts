import createClient from "openapi-fetch"
import { z } from "zod"
import { accessToken } from "@/lib/auth/session"
import { jsonObjectSchema, parseJson } from "@/lib/json"
import type { paths as PublicPaths } from "./schema"
import type { paths as UiPaths, components } from "./ui-schema"

export const publicApi = createClient<PublicPaths>()
export const uiApi = createClient<UiPaths>()
for (const client of [publicApi, uiApi]) {
  client.use({
    async onRequest({ request }) {
      const token = await accessToken()
      if (token) {
        request.headers.set("Authorization", `Bearer ${token}`)
      }
      return new Request(request, { cache: "no-store" })
    },
  })
}

const problemSchema = z.object({
  title: z.string().optional(),
  detail: z.string().optional(),
  errors: z
    .array(
      z.object({
        path: z.string(),
        message: z.string(),
        hint: z.string().nullable().optional(),
      })
    )
    .optional(),
})

export class ApiError extends Error {
  readonly status: number
  readonly fields: ReadonlyMap<string, string>

  constructor(response: Response, body: unknown) {
    const problem = problemSchema.safeParse(body)
    const fields = new Map(
      problem.success
        ? problem.data.errors?.map((error) => [
            error.path,
            error.hint ?? error.message,
          ])
        : []
    )
    const details = problem.success
      ? (problem.data.detail ?? problem.data.title)
      : undefined
    super(
      response.status === 412
        ? "This schedule changed elsewhere. Your edits are preserved. Reload the latest schedule before trying again."
        : [
            details ?? `API request failed: HTTP ${response.status}.`,
            ...fields.values(),
          ].join(" ")
    )
    this.status = response.status
    this.fields = fields
  }
}

export type ScheduleRequest = components["schemas"]["UserScheduleRequest"]
export const scheduleSchema = z.object({
  id: z.uuid(),
  seriesId: z.uuid(),
  workflowId: z.string(),
  name: z.string(),
  origin: z.enum(["User", "Code"]),
  timezone: z.string(),
  cron: z.string().nullable(),
  interval: z.string().nullable(),
  versionPin: z.string().nullable(),
  overlap: z.enum(["Skip", "Queue", "Concurrent"]),
  catchUp: z.enum(["None", "Latest"]),
  variables: jsonObjectSchema,
  paused: z.boolean(),
  rowVersion: z.int().positive(),
  nextFireAt: z.iso.datetime({ offset: true }).nullable(),
})
export type Schedule = z.infer<typeof scheduleSchema>
export interface ScheduleSnapshot {
  readonly schedule: Schedule
  readonly etag: string
}

function snapshot(response: Response, body: unknown): ScheduleSnapshot {
  if (typeof body !== "string") {
    throw new Error("The API returned an empty schedule response.")
  }
  const schedule = scheduleSchema.parse(parseJson(body))
  const etag = response.headers.get("ETag")
  if (!etag || etag !== `"${schedule.rowVersion}"`) {
    throw new Error(
      "The API returned an inconsistent schedule ETag. Reload before editing."
    )
  }
  return { schedule, etag }
}

export async function getSchedule(
  id: string,
  signal?: AbortSignal
): Promise<ScheduleSnapshot> {
  const result = await publicApi.GET("/api/v1/schedules/{id}", {
    params: { path: { id } },
    signal,
    parseAs: "text",
  })
  if (!result.response.ok) {
    throw new ApiError(result.response, result.error)
  }
  return snapshot(result.response, result.data)
}

export async function previewSchedule(
  body: ScheduleRequest,
  signal?: AbortSignal
): Promise<string[]> {
  const result = await uiApi.POST("/api/v1/schedules/preview", { body, signal })
  if (!result.response.ok) {
    throw new ApiError(result.response, result.error)
  }
  return z
    .object({ fires: z.array(z.iso.datetime({ offset: true })).length(5) })
    .parse(result.data).fires
}

export async function saveSchedule(
  body: ScheduleRequest,
  current?: ScheduleSnapshot
): Promise<ScheduleSnapshot> {
  const result = current
    ? await uiApi.PUT("/api/v1/schedules/{id}", {
        params: { path: { id: current.schedule.id } },
        body,
        headers: { "If-Match": current.etag },
        parseAs: "text",
      })
    : await uiApi.POST("/api/v1/schedules", { body, parseAs: "text" })
  if (!result.response.ok) {
    throw new ApiError(result.response, result.error)
  }
  return snapshot(result.response, result.data)
}

export async function setSchedulePaused(
  current: ScheduleSnapshot
): Promise<ScheduleSnapshot> {
  const path = current.schedule.paused
    ? "/api/v1/schedules/{id}/resume"
    : "/api/v1/schedules/{id}/pause"
  const result = await uiApi.POST(path, {
    params: { path: { id: current.schedule.id } },
    headers: { "If-Match": current.etag },
    parseAs: "text",
  })
  if (!result.response.ok) {
    throw new ApiError(result.response, result.error)
  }
  return snapshot(result.response, result.data)
}

export async function deleteSchedule(current: ScheduleSnapshot): Promise<void> {
  const result = await uiApi.DELETE("/api/v1/schedules/{id}", {
    params: { path: { id: current.schedule.id } },
    headers: { "If-Match": current.etag },
  })
  if (!result.response.ok) {
    throw new ApiError(result.response, result.error)
  }
}

export const seriesFiresSchema = z.object({
  seriesId: z.uuid(),
  items: z
    .array(
      z.object({
        fireAt: z.iso.datetime({ offset: true }),
        outcome: z.enum(["Started", "Skipped", "Missed", "CaughtUp"]),
        runId: z.uuid().nullable(),
        reason: z.string().nullable(),
      })
    )
    .max(100),
  nextCursor: z
    .string()
    .regex(/^[A-Za-z0-9_-]{11}$/)
    .nullable(),
})

export async function getSeriesFires(
  id: string,
  cursor?: string,
  signal?: AbortSignal
) {
  const result = await publicApi.GET("/api/v1/series/{id}/fires", {
    params: { path: { id }, query: { cursor } },
    signal,
  })
  if (!result.response.ok) {
    throw new ApiError(result.response, result.error)
  }
  const page = seriesFiresSchema.parse(result.data)
  if (page.seriesId !== id) {
    throw new Error("The API returned fire history for a different series.")
  }
  return page
}
