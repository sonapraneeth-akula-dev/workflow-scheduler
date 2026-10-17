import { z } from "zod"
import type { components } from "@/lib/api/schema"

const id = z.uuid()
const timestamp = z.iso.datetime({ offset: true })
export const liveAbortSchema = z.object({
  by: z.string().min(1),
  byName: z.string().min(1),
  at: timestamp,
  reason: z.string().min(1).max(500),
  source: z.enum(["UI", "API", "CLI", "KillSwitch", "Local"]),
})
export const liveEventSchema = z.object({
  seq: z.int().positive(),
  at: timestamp,
  entity: z.union([z.literal(0), z.literal(1), z.literal(2)]),
  key: z.string().min(1).nullable(),
  state: z.int().nonnegative(),
  abort: liveAbortSchema.nullable(),
  waitingReason: z.literal("WaitingForMachine").nullable().optional(),
  agentId: z.uuid().nullable().optional(),
})

const runDelta = z
  .object({ runId: id, events: z.array(liveEventSchema).min(1).max(512) })
  .superRefine((value, context) => {
    let previous: number | undefined
    for (const change of value.events) {
      if (
        (previous !== undefined && change.seq !== previous + 1) ||
        (change.entity === 0
          ? change.key !== null || change.state > 6
          : change.key === null || change.state > 11)
      ) {
        context.addIssue({
          code: "custom",
          message: "Invalid live event sequence or entity key.",
        })
      }
      previous = change.seq
    }
  })
const reportAdded = z.object({
  runId: id,
  reportId: id,
})
const summary = z.object({
  runId: id,
  workflowId: z.string().regex(/^[^/]+\/[^/]+\/[^/]+$/),
  versionId: id,
  seriesId: id.nullable(),
  status: z.int().min(0).max(6),
  lastSeq: z.int().nonnegative(),
  createdAt: timestamp,
  endedAt: timestamp.nullable(),
})

export const resourceSchema = z
  .object({
    scope: z.enum(["run", "type", "series", "team", "machines"]),
    id: z
      .string()
      .min(1)
      .max(1024)
      .regex(/^[^\p{Cc}]+$/u),
  })
  .transform((resource, context) => {
    if (resource.scope !== "run" && resource.scope !== "series") return resource
    const parsed = id.safeParse(resource.id)
    if (
      !parsed.success ||
      parsed.data === "00000000-0000-0000-0000-000000000000"
    ) {
      context.addIssue({
        code: "custom",
        message: "A canonical resource UUID is required.",
      })
      return z.NEVER
    }
    return { ...resource, id: parsed.data.toLowerCase() }
  })

const listDelta = z.object({
  scope: z.enum(["type", "series", "team"]),
  id: z.string().min(1).max(1024),
  items: z.array(summary),
})
const problem = z.object({
  status: z.int().min(400).max(599),
  title: z.string(),
  detail: z.string(),
  reason: z.string().nullable().optional(),
})

export type LiveResource = z.infer<typeof resourceSchema>
export type RunDelta = z.infer<typeof runDelta>
export type ListDelta = z.infer<typeof listDelta>
export type LiveDelta = RunDelta | ListDelta

export function parseRunDelta(value: unknown) {
  return runDelta.parse(value) satisfies components["schemas"]["RunDelta"]
}

export function parseReportAdded(value: unknown) {
  return reportAdded.parse(value)
}

export function parseListDelta(value: unknown) {
  return listDelta.parse(value) satisfies components["schemas"]["ListDelta"]
}

export function parseResync(value: unknown) {
  return resourceSchema.parse(
    value
  ) satisfies components["schemas"]["LiveResync"]
}

export function parseReconnect(value: unknown) {
  return z
    .object({ reason: z.literal("server_draining") })
    .parse(value) satisfies components["schemas"]["LiveReconnect"]
}

export function parseSubscription(value: unknown): Error | null {
  if (value === null) return null
  const result = problem.parse(
    value
  ) satisfies components["schemas"]["LiveSubscriptionProblem"]
  return new Error(`${result.title}: ${result.detail} (HTTP ${result.status})`)
}

export function resourceKey(resource: LiveResource): string {
  return `${resource.scope}:${resource.id}`
}
