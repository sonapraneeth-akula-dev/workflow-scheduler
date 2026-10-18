import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import { runIdSchema } from "@/lib/live/runApi"
import type { TriggerRequest } from "./trigger-api"

const optionsSchema = z.object({
  items: z.array(z.object({ id: z.uuid(), name: z.string().min(1).max(200) })),
})

export async function triggerPlacementOptions(
  request: TriggerRequest,
  signal: AbortSignal
) {
  const result = await publicApi.POST("/api/v1/runs/placement-options", {
    body: request,
    signal,
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  return optionsSchema.parse(result.data).items
}

export async function rerunPlacementOptions(
  runId: string,
  signal: AbortSignal
) {
  const result = await publicApi.POST("/api/v1/runs/{id}/placement-options", {
    params: { path: { id: runIdSchema.parse(runId) } },
    body: {},
    signal,
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  return optionsSchema.parse(result.data).items
}
