import { z } from "zod"

const failureType = z.enum(["Infra", "Service"])
const impact = z.enum(["Failed", "Retrying", "Optional", "Recovered"])
const category = z.string().min(1).max(64)
export const failureDetailSchema = z
  .object({
    type: failureType,
    category,
    message: z.string().min(1).max(4096),
    nodeKey: z.string().min(1),
    taskKey: z
      .string()
      .regex(/^[^/]+\/[^/]+$/)
      .nullable(),
    attempt: z.int().positive().max(32767).nullable(),
    impact,
    runtimeCategory: z.string().min(1).nullable(),
    ruleId: z.string().min(1).nullable(),
    messageRecorded: z.boolean(),
  })
  .refine(
    (value) =>
      (value.attempt === null || value.taskKey !== null) &&
      (value.taskKey === null || value.taskKey.split("/")[0] === value.nodeKey)
  )
export const failureSummarySchema = z
  .object({
    count: z.int().positive(),
    recoveredCount: z.int().nonnegative(),
    primary: failureDetailSchema,
    groups: z
      .array(
        z.object({
          type: failureType,
          category,
          impact,
          count: z.int().positive(),
        })
      )
      .min(1),
  })
  .refine(
    (value) =>
      value.groups.reduce((sum, group) => sum + group.count, 0) ===
        value.count &&
      value.groups
        .filter((group) => group.impact === "Recovered")
        .reduce((sum, group) => sum + group.count, 0) ===
        value.recoveredCount &&
      value.groups.some(
        (group) =>
          group.type === value.primary.type &&
          group.category === value.primary.category &&
          group.impact === value.primary.impact
      )
  )
export type FailureDetail = z.infer<typeof failureDetailSchema>
export type FailureSummaryData = z.infer<typeof failureSummarySchema>

export function failureLabels(summary: FailureSummaryData) {
  return summary.groups
    .map(
      (group) =>
        `${group.type} / ${group.category} (${group.impact}${group.count > 1 ? ` x${group.count}` : ""})`
    )
    .join("; ")
}
