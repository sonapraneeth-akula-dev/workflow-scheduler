import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import type { components } from "@/lib/api/schema"

const idSchema = z
  .uuid()
  .refine(
    (id) => id === id.toLowerCase() && id[14] === "7",
    "Use a canonical lowercase UUIDv7."
  )
const metadataText = (maxBytes: number) =>
  z
    .string()
    .refine(
      (value) =>
        value.trim().length > 0 &&
        new TextEncoder().encode(value).length <= maxBytes,
      `Use nonblank text of at most ${maxBytes} UTF-8 bytes.`
    )
const reportSchema = z.object({
  id: idSchema,
  runId: idSchema,
  sourceKey: z.string().min(1),
  attempt: z.int().positive(),
  kind: metadataText(128),
  title: metadataText(512),
  tags: z.array(metadataText(128)).max(32),
  sizeBytes: z.int().nonnegative().max(Number.MAX_SAFE_INTEGER),
  createdAt: z.iso.datetime({ offset: true }),
})
const indexSchema = z.object({
  runId: idSchema,
  items: z.array(reportSchema),
})
export type ReportItem = z.infer<typeof reportSchema>
export interface ReportViewData {
  report: ReportItem
  content: Blob
}

export async function getReportIndex(runId: string, signal?: AbortSignal) {
  const id = idSchema.parse(runId)
  const response = await publicApi.GET("/api/v1/runs/{id}/reports", {
    params: { path: { id } },
    signal,
  })
  if (!response.response.ok)
    throw new ApiError(response.response, response.error)
  const index = indexSchema.parse(response.data)
  const ids = new Set(index.items.map((item) => item.id))
  if (
    index.runId !== id ||
    index.items.some((item) => item.runId !== id) ||
    ids.size !== index.items.length
  )
    throw new Error("The API returned an inconsistent report index.")
  return index satisfies components["schemas"]["ReportIndexResponse"]
}

export async function getReportView(
  runId: string,
  reportId: string,
  signal?: AbortSignal
): Promise<ReportViewData> {
  const id = idSchema.parse(reportId)
  const index = await getReportIndex(runId, signal)
  const report = index.items.find((item) => item.id === id)
  if (!report) throw new Error("Report was not found in this run.")
  const response = await publicApi.GET("/api/v1/reports/{id}/content", {
    params: { path: { id } },
    parseAs: "blob",
    signal,
  })
  if (!response.response.ok)
    throw new ApiError(response.response, response.error)
  const content = response.data
  if (!(content instanceof Blob) || content.size !== report.sizeBytes)
    throw new Error("Report content does not match its committed byte size.")
  return { report, content }
}
