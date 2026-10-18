import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import type { components } from "@/lib/api/schema"
import { jsonObjectSchema, parseJson } from "@/lib/json"

const runIdentity = z
  .uuid()
  .refine(
    (value) => value === value.toLowerCase() && value[14] === "7",
    "Use a canonical lowercase run UUIDv7."
  )
const taskIdentity = z.string().refine((value) => {
  const parts = value.split("/")
  return (
    parts.length === 2 &&
    parts.every(
      (part) =>
        part.trim().length > 0 && new TextEncoder().encode(part).length <= 512
    )
  )
})
const attemptNumber = z.int().min(1).max(32767)
const responseSchema = z.object({
  runId: runIdentity,
  taskKey: taskIdentity,
  attempt: attemptNumber,
  outputs: jsonObjectSchema.nullable(),
})
export type TaskOutputsResult = z.infer<typeof responseSchema>

export async function getTaskOutputs(
  runId: string,
  attempt: { readonly taskKey: string; readonly attempt: number },
  signal?: AbortSignal
): Promise<TaskOutputsResult> {
  const id = runIdentity.parse(runId)
  const taskKey = taskIdentity.parse(attempt.taskKey)
  const number = attemptNumber.parse(attempt.attempt)
  const response = await publicApi.GET("/api/v1/runs/{id}/outputs", {
    params: { path: { id }, query: { taskKey, attempt: number } },
    signal,
    cache: "no-store",
    parseAs: "text",
  })
  if (!response.response.ok)
    throw new ApiError(response.response, response.error)
  if (typeof response.data !== "string")
    throw new Error("The API returned no task outputs response.")
  const result = responseSchema.parse(parseJson(response.data))
  if (
    result.runId !== id ||
    result.taskKey !== taskKey ||
    result.attempt !== number
  )
    throw new Error("The API returned outputs for a different task attempt.")
  return result satisfies components["schemas"]["TaskOutputsResponse"]
}
