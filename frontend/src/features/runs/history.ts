import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import type { components } from "@/lib/api/schema"
import { runStatuses } from "@/features/catalog/api"
import { failureSummarySchema } from "./failures"
import {
  instantMicros,
  instantSchema,
  metadataValuesSchema,
  runFilterSchema,
} from "./search"

const principal = z
  .string()
  .regex(
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/,
    "Use a canonical lowercase nonempty principal UUID."
  )
  .refine(
    (value) =>
      value === value.toLowerCase() &&
      value !== "00000000-0000-0000-0000-000000000000",
    "Use a canonical lowercase nonempty principal UUID."
  )
export const historySearchSchema = z.object({
  status: z.enum(runStatuses).optional(),
  abortedBy: principal.optional(),
  sort: z.enum(["createdAtDesc", "createdAtAsc"]).optional(),
  outcome: z.enum(["Run", "Skipped", "Missed"]).optional(),
})
export type HistorySearch = z.infer<typeof historySearchSchema>
export type HistoryScope =
  | { workflowId: string; seriesId?: never }
  | { seriesId: string; workflowId?: never }

const rowSchema = z.object({
  kind: z.enum(["Run", "Fire"]),
  at: instantSchema,
  workflowId: runFilterSchema.shape.workflowId.unwrap(),
  seriesId: z.uuid().nullable(),
  runId: z.uuid().nullable(),
  versionId: z.uuid().nullable(),
  version: z.string().min(1).max(256).nullable(),
  status: z.enum(runStatuses).nullable(),
  failureSummary: failureSummarySchema.nullish(),
  startedAt: instantSchema.nullable(),
  endedAt: instantSchema.nullable(),
  durationMs: z.number().finite().nonnegative().nullable(),
  trigger: z.enum(["Manual", "Schedule", "CatchUp", "Rerun"]).nullable(),
  metadata: z
    .object({ type: z.string().min(1), values: metadataValuesSchema })
    .nullable(),
  abort: z
    .object({
      by: principal,
      byName: z.string(),
      at: z.iso
        .datetime({ offset: true })
        .refine((value) => Number.isFinite(Date.parse(value))),
      reason: z.string(),
      source: z.string(),
    })
    .nullable(),
  variables: z
    .array(
      z.object({
        key: z.string().min(1),
        type: z.enum([
          "String",
          "Int",
          "Long",
          "Double",
          "Bool",
          "Enum",
          "StringList",
          "Duration",
          "Secret",
        ]),
        redacted: z.boolean(),
      })
    )
    .refine(
      (values) =>
        new Set(values.map((value) => value.key)).size === values.length &&
        values.every((value) => value.redacted === (value.type === "Secret"))
    ),
  fire: z
    .object({
      scheduleId: z.uuid(),
      fireAt: instantSchema,
      outcome: z.enum(["Started", "Skipped", "Missed", "CaughtUp"]),
      reason: z.string().nullable(),
      scheduleConfigVersionId: z.uuid().nullable(),
    })
    .nullable(),
})
export type HistoryRow = z.infer<typeof rowSchema>
const pageSchema = z.object({
  items: z.array(rowSchema).max(50),
  nextCursor: z.string().min(1).max(256).nullable(),
  currentScheduleId: z.uuid().nullable(),
})
export type HistoryPage = z.infer<typeof pageSchema>
export type HistoryCursor = { cursor: string; after: HistoryRow } | null

function compare(a: HistoryRow, b: HistoryRow): number {
  const time = instantMicros(a.at) - instantMicros(b.at)
  if (time !== 0n) return time < 0n ? -1 : 1
  const idA = a.kind === "Run" ? a.runId! : a.fire!.scheduleId
  const idB = b.kind === "Run" ? b.runId! : b.fire!.scheduleId
  return idA === idB
    ? a.kind === b.kind
      ? 0
      : a.kind === "Run"
        ? -1
        : 1
    : idA < idB
      ? -1
      : 1
}

export async function getHistory(
  scope: HistoryScope,
  search: HistorySearch,
  cursor: HistoryCursor,
  signal?: AbortSignal
): Promise<HistoryPage> {
  const filters = historySearchSchema.parse(search)
  const resource: HistoryScope = z
    .union([
      z.strictObject({ workflowId: runFilterSchema.shape.workflowId.unwrap() }),
      z.strictObject({ seriesId: z.uuid() }),
    ])
    .parse(scope)
  const result = await publicApi.GET("/api/v1/runs/history", {
    params: {
      query: { ...resource, ...filters, limit: 50, cursor: cursor?.cursor },
    },
    signal,
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  const page = pageSchema.parse(result.data)
  const ascending = filters.sort === "createdAtAsc"
  if (
    page.nextCursor === cursor?.cursor ||
    (page.nextCursor !== null && page.items.length === 0) ||
    (!("seriesId" in resource) && page.currentScheduleId !== null) ||
    page.items.some((row, index) => {
      const previous = index ? page.items[index - 1]! : cursor?.after
      const isRun = row.kind === "Run"
      const duration =
        row.startedAt && row.endedAt
          ? Number(instantMicros(row.endedAt) - instantMicros(row.startedAt)) /
            1000
          : null
      return (
        (resource.workflowId !== undefined &&
          row.workflowId !== resource.workflowId) ||
        (resource.seriesId !== undefined &&
          row.seriesId !== resource.seriesId) ||
        (isRun
          ? !row.runId || !row.versionId || !row.status || !row.trigger
          : !row.fire ||
            !["Skipped", "Missed"].includes(row.fire.outcome) ||
            row.versionId !== null ||
            row.version !== null ||
            row.status !== null ||
            row.trigger !== null ||
            row.startedAt !== null ||
            row.endedAt !== null ||
            row.abort !== null ||
            row.variables.length !== 0) ||
        (row.kind === "Fire" &&
          row.fire !== null &&
          instantMicros(row.at) !== instantMicros(row.fire.fireAt)) ||
        (duration === null
          ? row.durationMs !== null
          : row.durationMs === null ||
            Math.abs(duration - row.durationMs) > 0.000001) ||
        (filters.status !== undefined && row.status !== filters.status) ||
        (filters.abortedBy !== undefined &&
          row.abort?.by !== filters.abortedBy) ||
        (filters.outcome === "Run" && !isRun) ||
        (filters.outcome !== undefined &&
          filters.outcome !== "Run" &&
          (isRun || row.fire?.outcome !== filters.outcome)) ||
        (previous !== undefined &&
          (ascending
            ? compare(previous, row) >= 0
            : compare(previous, row) <= 0))
      )
    })
  )
    throw new Error("The API returned inconsistent history for this resource.")
  return page satisfies components["schemas"]["RunHistoryPage"]
}
