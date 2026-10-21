import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import type { components } from "@/lib/api/schema"
import { runStatuses } from "@/features/catalog/api"
import { failureSummarySchema } from "./failures"
import { signOffStateSchema } from "./signoff-api"
import {
  instantMicros,
  instantSchema,
  metadataValuesSchema,
  runFilterSchema,
  runSearchSchema,
  type RunFilters,
} from "./search"

const rowSchema = z.object({
  id: z.uuid(),
  workflowId: z.string().min(1),
  versionId: z.uuid(),
  seriesId: z.uuid().nullable(),
  team: z.string().min(1),
  type: z.string().min(1),
  status: z.enum(runStatuses),
  createdAt: instantSchema,
  startedAt: instantSchema.nullable(),
  endedAt: instantSchema.nullable(),
  failureSummary: failureSummarySchema.nullish(),
  signOff: signOffStateSchema,
  metadata: z
    .object({
      type: z.string().min(1),
      values: metadataValuesSchema,
    })
    .nullable(),
})
const pageSchema = z.object({
  items: z.array(rowSchema).max(50),
  nextCursor: z.string().min(1).max(8192).nullable(),
})
export type RunRow = z.infer<typeof rowSchema>
export type RunPage = z.infer<typeof pageSchema>
export type RunPageCursor = { cursor: string; afterId: string } | null

export async function getRuns(
  filters: RunFilters,
  cursor: RunPageCursor,
  signal?: AbortSignal
): Promise<RunPage> {
  const parsed = runSearchSchema.parse(runFilterSchema.parse(filters))
  const result = await publicApi.GET("/api/v1/runs", {
    params: {
      query: {
        team: parsed.team,
        type: parsed.type,
        workflowId: parsed.workflowId,
        seriesId: parsed.seriesId,
        status: parsed.status,
        from: parsed.from,
        to: parsed.to,
        limit: 50,
        cursor: cursor?.cursor,
        ...Object.fromEntries(
          Object.entries(parsed.metadata ?? {}).map(([key, value]) => [
            `meta.${key}`,
            value,
          ])
        ),
      },
    },
    signal,
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  const page = pageSchema.parse(result.data)
  if (
    page.nextCursor === cursor?.cursor ||
    (page.nextCursor !== null && page.items.length === 0) ||
    page.items.some((row, index) => {
      const [team, type, workflow, extra] = row.workflowId.split("/")
      const previous = index ? page.items[index - 1]!.id : cursor?.afterId
      return (
        !workflow ||
        extra !== undefined ||
        team !== row.team ||
        type !== row.type ||
        (previous !== undefined && row.id >= previous) ||
        (parsed.team !== undefined && row.team !== parsed.team) ||
        (parsed.type !== undefined && row.type !== parsed.type) ||
        (parsed.workflowId !== undefined &&
          row.workflowId !== parsed.workflowId) ||
        (parsed.seriesId !== undefined && row.seriesId !== parsed.seriesId) ||
        (parsed.status !== undefined && row.status !== parsed.status) ||
        (parsed.from !== undefined &&
          instantMicros(row.createdAt) < instantMicros(parsed.from)) ||
        (parsed.to !== undefined &&
          instantMicros(row.createdAt) >= instantMicros(parsed.to)) ||
        Object.entries(parsed.metadata ?? {}).some(
          ([key, value]) =>
            !row.metadata ||
            !Object.hasOwn(row.metadata.values, key) ||
            row.metadata.values[key] !== value
        )
      )
    })
  )
    throw new Error("The API returned an inconsistent filtered run page.")
  return page satisfies components["schemas"]["RunQueryPage"]
}
