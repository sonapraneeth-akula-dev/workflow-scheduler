import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import type { components } from "@/lib/api/schema"

const runIdentity = z
  .uuid()
  .refine((value) => value === value.toLowerCase() && value[14] === "7")
const sequence = z
  .string()
  .regex(/^(0|[1-9]\d*)$/)
  .refine((value) => BigInt(value) <= 9223372036854775807n)
const time = z.iso.datetime({ offset: true }).nullable()
const conditionSchema = z
  .strictObject({
    entityKey: z.string().min(1).max(1025),
    variableKey: z.string().min(1).nullable(),
    nodeKey: z.string().min(1).nullable(),
    outputKey: z.string().min(1).nullable(),
    expected: z.boolean(),
    actual: z.boolean().nullable(),
    outcome: z
      .enum(["Matched", "NotMatched", "MissingValue", "InvalidType"])
      .nullable(),
    evaluatedAt: time,
    evaluatedSequence: sequence.nullable(),
    sourceKind: z.enum(["RunVariable", "NodeOutput"]),
    declaredDefault: z.boolean().nullable(),
    sourceRunId: runIdentity,
    producerTaskKey: z.string().min(1).nullable(),
    producerAttempt: z.int().min(1).max(32767).nullable(),
    producedAt: time,
  })
  .refine((value) => {
    const sourceValid =
      value.sourceKind === "RunVariable"
        ? value.variableKey !== null &&
          value.nodeKey === null &&
          value.outputKey === null &&
          value.producerTaskKey === null
        : value.variableKey === null &&
          value.nodeKey !== null &&
          value.outputKey !== null &&
          value.declaredDefault === null
    const actualValid =
      value.outcome === "Matched"
        ? value.actual === value.expected
        : value.outcome === "NotMatched"
          ? value.actual === !value.expected
          : value.actual === null
    return (
      sourceValid &&
      actualValid &&
      (value.producerTaskKey === null ||
        value.actual !== null ||
        value.outcome === "InvalidType") &&
      (value.evaluatedAt === null) === (value.evaluatedSequence === null) &&
      (value.outcome !== null || value.evaluatedAt === null) &&
      (value.producerTaskKey === null) === (value.producerAttempt === null) &&
      (value.producerTaskKey === null) === (value.producedAt === null)
    )
  }, "Inconsistent condition evidence.")
const responseSchema = z
  .strictObject({
    runId: runIdentity,
    versionId: z.uuid(),
    lastSeq: sequence,
    conditions: z.array(conditionSchema),
  })
  .refine(
    (value) =>
      new Set(value.conditions.map((item) => item.entityKey)).size ===
        value.conditions.length &&
      value.conditions.every(
        (item) =>
          item.evaluatedSequence === null ||
          BigInt(item.evaluatedSequence) <= BigInt(value.lastSeq)
      )
  )
export type ConditionDebugDetails = z.infer<typeof conditionSchema>
export type RunConditions = z.infer<typeof responseSchema>

export async function getRunConditions(
  runId: string,
  versionId: string,
  signal?: AbortSignal
): Promise<RunConditions> {
  const id = runIdentity.parse(runId)
  const version = z.uuid().parse(versionId)
  const response = await publicApi.GET("/api/v1/runs/{id}/conditions", {
    params: { path: { id } },
    signal,
    cache: "no-store",
  })
  if (!response.response.ok)
    throw new ApiError(response.response, response.error)
  const result = responseSchema.parse(response.data)
  if (result.runId !== id || result.versionId !== version)
    throw new Error(
      "The API returned condition evidence for a different run or version."
    )
  return result satisfies components["schemas"]["RunConditionsResponse"]
}
