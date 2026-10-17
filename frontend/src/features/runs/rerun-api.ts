import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import type { components } from "@/lib/api/schema"
import { runIdSchema } from "@/lib/live/runApi"

export async function rerunRun(parentRunId: string, key: string) {
  const id = runIdSchema.parse(parentRunId)
  z.uuid().parse(key)
  const result = await publicApi.POST("/api/v1/runs/{id}/rerun", {
    params: { path: { id } },
    body: {},
    headers: { "Idempotency-Key": key },
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  if (result.response.status !== 202)
    throw new Error(
      "The API did not acknowledge rerun acceptance with HTTP 202."
    )
  const receipt = z.object({ runId: runIdSchema }).parse(result.data)
  if (receipt.runId === id)
    throw new Error("The API returned the source run instead of a new rerun.")
  return receipt satisfies components["schemas"]["TriggerRunResponse"]
}
