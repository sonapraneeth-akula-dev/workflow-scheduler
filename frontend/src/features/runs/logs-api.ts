import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import type { components } from "@/lib/api/schema"
import { liveLogRecordSchema, logIdentitySchema } from "@/lib/live/logs"

const frame = liveLogRecordSchema.omit({ line: true })
const pageSchema = z.object({
  totalLines: z.int().nonnegative(),
  droppedLines: z.int().nonnegative(),
  truncated: z.boolean(),
  captureInterrupted: z.boolean(),
  truncationMarker: z.string().nullable(),
  records: z.array(z.object({ line: z.int().positive(), frame })).max(1000),
  matches: z
    .array(
      z.object({
        line: z.int().positive(),
        index: z.int().nonnegative(),
        length: z.int().nonnegative(),
        frame,
      })
    )
    .max(1000),
  nextCursor: z.string().nullable(),
})
export type TaskLogsPage = z.infer<typeof pageSchema>
export type TaskLogsResult =
  { state: "ready"; page: TaskLogsPage } | { state: "uploading" }

export function logIdentity(
  runId: string,
  attempt: { taskKey: string; attempt: number }
) {
  return logIdentitySchema.parse(
    `${runId}/${attempt.taskKey}/${attempt.attempt}`
  )
}

export async function getTaskLogs(
  runId: string,
  attempt: { taskKey: string; attempt: number },
  offset: number,
  length: number,
  signal?: AbortSignal
): Promise<TaskLogsResult> {
  logIdentity(runId, attempt)
  z.int()
    .nonnegative()
    .max(Number.MAX_SAFE_INTEGER - 1000)
    .parse(offset)
  z.int().min(1).max(1000).parse(length)
  const result = await publicApi.GET(
    "/api/v1/runs/{id}/tasks/{task}/{attempt}/logs",
    {
      params: {
        path: {
          id: runId,
          task: attempt.taskKey,
          attempt: String(attempt.attempt),
        },
        query: { offset: String(offset), length: String(length) },
      },
      signal,
    }
  )
  if (!result.response.ok) {
    const problem = z
      .object({ reason: z.enum(["log_not_ready", "log_not_available"]) })
      .safeParse(result.error)
    if (result.response.status === 409 && problem.success)
      return { state: "uploading" }
    throw new ApiError(result.response, result.error)
  }
  const page = pageSchema.parse(result.data)
  if (
    page.droppedLines > page.totalLines ||
    page.records.some(
      (record, index) =>
        record.line <= offset ||
        record.line > offset + length ||
        record.line > page.totalLines ||
        (index > 0 && record.line <= (page.records[index - 1]?.line ?? 0))
    )
  )
    throw new Error("The API returned an inconsistent task log range.")
  page satisfies components["schemas"]["TaskLogsPage"]
  return { state: "ready", page }
}
