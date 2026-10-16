import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import type { components } from "@/lib/api/schema"
import { liveAbortSchema, liveEventSchema } from "./messages"

const runSnapshotSchema = z
  .object({
    runId: z.uuid(),
    workflowId: z.string().regex(/^[^/]+\/[^/]+\/[^/]+$/),
    versionId: z.uuid(),
    seriesId: z.uuid().nullable(),
    status: z.int().min(0).max(6),
    lastSeq: z.int().nonnegative(),
    nodes: z.record(z.string().min(1), z.int().min(0).max(11)),
    tasks: z.record(z.string().regex(/^[^/]+\/[^/]+$/), z.int().min(0).max(11)),
    abort: liveAbortSchema.nullable(),
    endedAt: z.iso.datetime({ offset: true }).nullable(),
  })
  .superRefine((snapshot, context) => {
    if (
      Object.keys(snapshot.tasks).some(
        (key) => !Object.hasOwn(snapshot.nodes, key.slice(0, key.indexOf("/")))
      )
    )
      context.addIssue({
        code: "custom",
        message: "A run task has no matching node.",
      })
  })
const runEventsSchema = z.object({
  runId: z.uuid(),
  lastSeq: z.int().nonnegative(),
  events: z.array(liveEventSchema).max(5000),
})

export type RunSnapshot = z.infer<typeof runSnapshotSchema>
export type RunEvents = z.infer<typeof runEventsSchema>

export async function readRunSnapshot(runId: string, signal: AbortSignal) {
  const result = await publicApi.GET("/api/v1/runs/{id}", {
    params: { path: { id: runId } },
    signal,
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  const snapshot = runSnapshotSchema.parse(result.data)
  if (snapshot.runId !== runId)
    throw new Error("The API returned a different run's state.")
  return snapshot satisfies components["schemas"]["RunSnapshotResponse"]
}

export async function readRunEvents(
  runId: string,
  afterSeq: number,
  signal: AbortSignal
) {
  z.int().nonnegative().parse(afterSeq)
  const result = await publicApi.GET("/api/v1/runs/{id}/events", {
    params: { path: { id: runId }, query: { afterSeq: String(afterSeq) } },
    signal,
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  const page = runEventsSchema.parse(result.data)
  if (
    page.runId !== runId ||
    page.lastSeq < afterSeq ||
    page.events.length !== page.lastSeq - afterSeq ||
    page.events.some(
      (event, index) =>
        event.seq !== afterSeq + index + 1 ||
        (event.entity === 0
          ? event.key !== null || event.state > 6
          : event.key === null || event.state > 11)
    )
  )
    throw new Error("The API returned an inconsistent run event sequence.")
  return page satisfies components["schemas"]["RunEventsPage"]
}
