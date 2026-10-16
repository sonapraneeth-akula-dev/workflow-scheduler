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
