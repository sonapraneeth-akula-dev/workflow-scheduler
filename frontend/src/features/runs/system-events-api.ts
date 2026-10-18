import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import type { components } from "@/lib/api/schema"
import { runIdSchema } from "@/lib/live/runApi"

const sequence = z
  .string()
  .regex(/^(0|[1-9][0-9]{0,18})$/)
  .refine((value) => BigInt(value) <= 9223372036854775807n)
const timestamp = z.iso.datetime({ offset: true })
const key = z
  .string()
  .min(1)
  .refine(
    (value) =>
      value.trim().length > 0 &&
      !/[/\p{Cc}]/u.test(value) &&
      new TextEncoder().encode(value).length <= 512
  )
export const systemEventsFilters = z
  .object({
    node: key.optional(),
    task: z
      .string()
      .refine((value) => {
        const parts = value.split("/")
        return (
          parts.length === 2 &&
          parts.every((part) => key.safeParse(part).success)
        )
      })
      .optional(),
    attempt: z
      .string()
      .regex(/^[1-9][0-9]{0,4}$/)
      .refine((value) => Number(value) <= 32767)
      .optional(),
  })
  .strict()
  .refine(
    (filters) =>
      !filters.node ||
      !filters.task ||
      filters.task.split("/")[0] === filters.node,
    "Node and full task key must agree."
  )
export type SystemEventsFilters = z.infer<typeof systemEventsFilters>

const eventSchema = z
  .object({
    seq: sequence.refine((value) => value !== "0"),
    at: timestamp,
    entity: z.int().min(0).max(2),
    key: z.string().min(1).nullable(),
    state: z.int().min(0).max(11),
    attempt: z.int().min(1).max(32767).nullable(),
    agentId: z.uuid().nullable(),
    selectionReason: z
      .enum(["WarmHost", "LeastLoaded", "RoundRobin", "Affinity"])
      .nullable(),
    plannedAgentId: z.uuid().nullable(),
    waitingReason: z.literal("WaitingForMachine").nullable(),
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
    progress: z
      .object({
        sequence: z.int().min(1).max(64),
        phase: z.int().min(1).max(9),
        occurredAt: timestamp,
      })
      .strict()
      .nullable(),
  })
  .strict()
  .refine(
    (event) =>
      (event.entity === 0
        ? event.key === null && event.state <= 6 && event.attempt === null
        : event.key !== null &&
          (event.entity !== 2 || event.key.split("/").length === 2)) &&
      (!(event.selectionReason || event.plannedAgentId) ||
        (!!event.selectionReason &&
          event.entity === 2 &&
          event.state === 2 &&
          !!event.agentId &&
          !!event.attempt &&
          !event.progress &&
          !event.waitingReason)) &&
      (!event.progress ||
        (event.entity === 2 &&
          !!event.attempt &&
          [2, 3].includes(event.state) &&
          !event.agentId &&
          !event.waitingReason)) &&
      (!event.agentId || (event.entity === 2 && event.state === 2)),
    "Inconsistent system event metadata."
  )
const pageSchema = z
  .object({
    runId: runIdSchema,
    workflowId: z.string().regex(/^[^/]+\/[^/]+\/[^/]+$/),
    versionId: z.uuid(),
    lastSeq: sequence,
    events: z.array(eventSchema).max(200),
    nextCursor: z.string().min(95).max(256).nullable(),
  })
  .strict()
export type SystemEvent = z.infer<typeof eventSchema>
export type SystemEventsPage = z.infer<typeof pageSchema>

export async function getSystemEvents(
  runId: string,
  filters: SystemEventsFilters,
  cursor?: string,
  signal?: AbortSignal
): Promise<SystemEventsPage> {
  const id = runIdSchema.parse(runId)
  const query = systemEventsFilters.parse(filters)
  const response = await publicApi.GET("/api/v1/runs/{id}/system-events", {
    params: { path: { id }, query: { ...query, limit: "10", cursor } },
    signal,
  })
  if (!response.response.ok)
    throw new ApiError(response.response, response.error)
  const page = pageSchema.parse(response.data)
  let previous = 0n
  if (
    page.runId !== id ||
    page.events.length > 10 ||
    (page.nextCursor !== null &&
      (page.nextCursor === cursor || page.events.length !== 10)) ||
    page.events.some((event) => {
      const value = BigInt(event.seq)
      const invalid =
        value <= previous ||
        value > BigInt(page.lastSeq) ||
        (!!query.node &&
          (event.entity === 0 || event.key?.split("/")[0] !== query.node)) ||
        (!!query.task && (event.entity !== 2 || event.key !== query.task)) ||
        (!!query.attempt && event.attempt !== Number(query.attempt))
      previous = value
      return invalid
    })
  )
    throw new Error("The API returned inconsistent paged system events.")
  return page satisfies components["schemas"]["SystemEventsPage"]
}
