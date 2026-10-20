import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import type { components } from "@/lib/api/schema"
import { runStatuses } from "@/features/catalog/api"
import { failureDetailSchema, failureSummarySchema } from "./failures"

const timestamp = z.iso
  .datetime({ offset: true })
  .refine(
    (value) => !/\.\d{8}/.test(value) && Number.isFinite(Date.parse(value)),
    "Use a valid timestamp with at most seven fractional digits."
  )
const attemptSchema = z.object({
  nodeKey: z.string().min(1),
  taskKey: z.string().regex(/^[^/]+\/[^/]+$/),
  attempt: z.int().positive().max(32767),
  queuedAt: timestamp,
  dispatchedAt: timestamp,
  startedAt: timestamp.nullable(),
  endedAt: timestamp.nullable(),
  queueWaitMs: z.number().finite().nonnegative(),
  durationMs: z.number().finite().nonnegative(),
  terminalStatus: z
    .enum([
      "Succeeded",
      "SoftFailed",
      "Failed",
      "TimedOut",
      "Cancelled",
      "Skipped",
      "Reused",
    ])
    .nullable(),
  failureCategory: z
    .enum([
      "TaskError",
      "Timeout",
      "Aborted",
      "AgentLost",
      "ResourceLimit",
      "ValidationError",
      "Placement",
      "PlatformError",
    ])
    .nullable(),
  agentId: z.string().min(1).nullable(),
  resumedFrom: timestamp
    .nullable()
    .refine(
      (value) => value === null || /(?:Z|\+00:00)$/.test(value),
      "Checkpoint resume time must be UTC."
    ),
})
const responseSchema = z.object({
  runId: z.uuid(),
  lastSeq: z.int().nonnegative(),
  status: z.enum(runStatuses),
  attempts: z.array(attemptSchema),
  failures: z.array(failureDetailSchema).optional(),
  failureSummary: failureSummarySchema.nullish(),
})
export type TimelineAttempt = z.infer<typeof attemptSchema>
export type RunTimeline = z.infer<typeof responseSchema>

export function instantTicks(value: string): bigint {
  const fraction = /\.(\d+)/.exec(value)?.[1] ?? ""
  return (
    BigInt(Date.parse(value)) * 10000n +
    BigInt(fraction.padEnd(7, "0").slice(3))
  )
}

export async function getTimeline(
  runId: string,
  signal?: AbortSignal
): Promise<RunTimeline> {
  const id = z
    .uuid()
    .refine(
      (value) => value === value.toLowerCase() && value[14] === "7",
      "Use a canonical lowercase run UUIDv7."
    )
    .parse(runId)
  const response = await publicApi.GET("/api/v1/runs/{id}/timeline", {
    params: { path: { id } },
    signal,
  })
  if (!response.response.ok)
    throw new ApiError(response.response, response.error)
  const result = responseSchema.parse(response.data)
  const attempts = new Map<string, number>()
  if (
    result.runId !== id ||
    (result.failures ?? []).some(
      (failure) =>
        failure.taskKey !== null &&
        (failure.attempt === null
          ? result.attempts.some(
              (attempt) => attempt.taskKey === failure.taskKey
            )
          : !result.attempts.some(
              (attempt) =>
                attempt.taskKey === failure.taskKey &&
                attempt.attempt === failure.attempt &&
                ["Failed", "SoftFailed", "TimedOut"].includes(
                  attempt.terminalStatus ?? ""
                )
            ))
    ) ||
    result.attempts.some((row) => {
      const previous = attempts.get(row.taskKey) ?? 0
      attempts.set(row.taskKey, row.attempt)
      const queued = instantTicks(row.queuedAt),
        dispatched = instantTicks(row.dispatchedAt)
      const started =
        row.startedAt === null ? null : instantTicks(row.startedAt)
      const ended = row.endedAt === null ? null : instantTicks(row.endedAt)
      const wait = Math.max(
        0,
        Number((started ?? ended ?? dispatched) - queued) / 10000
      )
      const duration =
        started !== null && ended !== null
          ? Math.max(0, Number(ended - started) / 10000)
          : 0
      return (
        row.taskKey.slice(0, row.taskKey.indexOf("/")) !== row.nodeKey ||
        row.attempt <= previous ||
        (row.resumedFrom !== null &&
          (row.attempt < 2 || instantTicks(row.resumedFrom) < 0n)) ||
        (row.endedAt === null) !== (row.terminalStatus === null) ||
        (row.failureCategory !== null &&
          !["SoftFailed", "Failed", "TimedOut", "Cancelled"].includes(
            row.terminalStatus ?? ""
          )) ||
        Math.abs(row.queueWaitMs - wait) > 0.000001 ||
        Math.abs(row.durationMs - duration) > 0.000001
      )
    })
  )
    throw new Error("The API returned an inconsistent committed run timeline.")
  return result satisfies components["schemas"]["RunTimelineResponse"]
}
