import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import type { components } from "@/lib/api/schema"
import { jsonObjectSchema, parseJson } from "@/lib/json"
import { triggerSchema } from "./variables"

export async function getTriggerSchema(
  workflowId: string,
  versionId?: string,
  signal?: AbortSignal
) {
  const [team, type, workflow, ...rest] = workflowId.split("/")
  if (!team || !type || !workflow || rest.length)
    throw new Error("An exact team/type/workflow identity is required.")
  if (versionId) z.uuid().parse(versionId)
  const result = await publicApi.GET(
    "/api/v1/workflows/{team}/{type}/{workflow}/variables",
    {
      params: { path: { team, type, workflow }, query: { version: versionId } },
      signal,
      parseAs: "text",
    }
  )
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  if (typeof result.data !== "string")
    throw new Error("The API returned an empty variable schema.")
  const schema = triggerSchema.parse(parseJson(result.data))
  if (
    schema.workflowId !== workflowId ||
    (versionId && schema.versionId !== versionId)
  )
    throw new Error(
      "The API returned variables for a different workflow version."
    )
  return schema satisfies components["schemas"]["WorkflowVariablesResponse"]
}

const requestSchema = z.object({
  workflowId: z.string().min(1),
  version: z.uuid(),
  variables: jsonObjectSchema,
})
export type TriggerRequest = z.infer<typeof requestSchema>
export const maximumTriggerBytes = 1_048_576

export function triggerRequestBytes(request: TriggerRequest): number {
  return new TextEncoder().encode(JSON.stringify(request)).length
}

export async function triggerRun(request: TriggerRequest, key: string) {
  const body = requestSchema.parse(
    request
  ) satisfies components["schemas"]["TriggerRunRequest"]
  z.uuid().parse(key)
  if (triggerRequestBytes(body) > maximumTriggerBytes)
    throw new Error(
      "The run request exceeds 1 MiB. Reduce its variable payload."
    )
  const result = await publicApi.POST("/api/v1/runs", {
    body,
    headers: { "Idempotency-Key": key },
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  if (result.response.status !== 202)
    throw new Error("The API did not acknowledge run acceptance with HTTP 202.")
  return z
    .object({ runId: z.uuid() })
    .parse(result.data) satisfies components["schemas"]["TriggerRunResponse"]
}
