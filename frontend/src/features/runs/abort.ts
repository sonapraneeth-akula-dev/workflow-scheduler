import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import { liveAbortSchema } from "@/lib/live/messages"
import type { components } from "@/lib/api/schema"

export const abortReasonSchema = z
  .string()
  .min(1)
  .max(500)
  .refine(
    (reason) => reason.trim().length > 0,
    "Supply a nonblank abort reason."
  )
export async function abortRun(runId: string, reason: string) {
  const id = z.uuid().parse(runId)
  const result = await publicApi.POST("/api/v1/runs/{id}/abort", {
    params: { path: { id } },
    body: { reason: abortReasonSchema.parse(reason), source: "UI" },
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  if (result.response.status !== 202)
    throw new Error(
      "The API did not acknowledge abort acceptance with HTTP 202."
    )
  return liveAbortSchema.parse(
    result.data
  ) satisfies components["schemas"]["AbortRunResponse"]
}
