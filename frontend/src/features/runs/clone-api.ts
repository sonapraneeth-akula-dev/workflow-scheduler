import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import type { components } from "@/lib/api/schema"
import { jsonObjectSchema, parseJson } from "@/lib/json"
import { runIdSchema, runPlacementSchema } from "@/lib/live/runApi"
import { maximumTriggerBytes } from "./trigger-api"
import { runFilterSchema } from "./search"

const sourceSchema = z.object({
  runId: runIdSchema,
  workflowId: runFilterSchema.shape.workflowId.unwrap(),
  versionId: z.uuid(),
  variables: jsonObjectSchema,
  placement: runPlacementSchema.nullable(),
})
export type CloneSource = z.infer<typeof sourceSchema>
const requestSchema = z.object({ variables: jsonObjectSchema })
export type CloneRequest = z.infer<typeof requestSchema>
export const cloneRequestBytes = (request: CloneRequest) =>
  new TextEncoder().encode(JSON.stringify(request)).length

export async function getCloneSource(
  expected: Pick<CloneSource, "runId" | "workflowId" | "versionId">,
  signal?: AbortSignal
) {
  runIdSchema.parse(expected.runId)
  const result = await publicApi.GET("/api/v1/runs/{id}/clone", {
    params: { path: { id: expected.runId } },
    parseAs: "text",
    signal,
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  if (typeof result.data !== "string")
    throw new Error("The API returned no clone source inputs.")
  const source = sourceSchema.parse(parseJson(result.data))
  if (
    source.runId !== expected.runId ||
    source.workflowId !== expected.workflowId ||
    source.versionId !== expected.versionId
  )
    throw new Error("The API returned a different run or pinned version.")
  return source satisfies components["schemas"]["CloneRunSourceResponse"]
}

export async function cloneRun(
  sourceRunId: string,
  request: CloneRequest,
  key: string
) {
  const id = runIdSchema.parse(sourceRunId)
  z.uuid().parse(key)
  const body = requestSchema.parse(
    request
  ) satisfies components["schemas"]["CloneRunRequest"]
  if (cloneRequestBytes(body) > maximumTriggerBytes)
    throw new Error(
      "The clone request exceeds 1 MiB. Reduce its variable payload."
    )
  const result = await publicApi.POST("/api/v1/runs/{id}/clone", {
    params: { path: { id } },
    body,
    headers: { "Idempotency-Key": key },
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  if (result.response.status !== 202)
    throw new Error(
      "The API did not acknowledge clone acceptance with HTTP 202."
    )
  const receipt = z.object({ runId: runIdSchema }).parse(result.data)
  if (receipt.runId === id)
    throw new Error("The API returned the source run instead of a new clone.")
  return receipt satisfies components["schemas"]["TriggerRunResponse"]
}
