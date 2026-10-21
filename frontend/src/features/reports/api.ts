import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import type { components } from "@/lib/api/schema"
import { signOffStateSchema } from "@/features/runs/signoff-api"

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
  sourceKey: z.string().regex(/^[^/]+\/[^/]+$/),
  attempt: z.int().positive(),
  kind: metadataText(128),
  title: metadataText(512),
  tags: z.array(metadataText(128)).max(32),
  sizeBytes: z.int().nonnegative().max(Number.MAX_SAFE_INTEGER),
  createdAt: z.iso.datetime({ offset: true }),
  signOff: signOffStateSchema,
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
export interface ReportComparisonQuery {
  leftRunId: string
  rightRunId: string
  sourceKey: string
  title: string
}
const seriesIdSchema = z
  .uuid()
  .refine(
    (id) =>
      id === id.toLowerCase() && id !== "00000000-0000-0000-0000-000000000000",
    "Use a canonical lowercase nonempty series UUID."
  )
const comparisonQuerySchema = z
  .object({
    leftRunId: idSchema,
    rightRunId: idSchema,
    sourceKey: metadataText(1025).and(z.string().regex(/^[^/]+\/[^/]+$/)),
    title: metadataText(512),
  })
  .refine((query) => query.leftRunId !== query.rightRunId, "Choose two runs.")
const comparisonSchema = z.object({
  seriesId: seriesIdSchema,
  left: reportSchema,
  right: reportSchema,
})

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
  return getReportContent(report, signal)
}

async function getReportContent(
  report: ReportItem,
  signal?: AbortSignal
): Promise<ReportViewData> {
  const response = await publicApi.GET("/api/v1/reports/{id}/content", {
    params: { path: { id: report.id } },
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

export async function getReportComparison(
  seriesId: string,
  selection: ReportComparisonQuery,
  signal?: AbortSignal
) {
  const id = seriesIdSchema.parse(seriesId)
  const query = comparisonQuerySchema.parse(selection)
  const response = await publicApi.GET("/api/v1/series/{id}/reports/compare", {
    params: { path: { id }, query },
    signal,
  })
  if (!response.response.ok)
    throw new ApiError(response.response, response.error)
  const pair = comparisonSchema.parse(response.data)
  if (
    pair.seriesId !== id ||
    pair.left.runId !== query.leftRunId ||
    pair.right.runId !== query.rightRunId ||
    pair.left.id === pair.right.id ||
    [pair.left, pair.right].some(
      (report) =>
        report.sourceKey !== query.sourceKey || report.title !== query.title
    )
  )
    throw new Error("The API returned an inconsistent report comparison.")
  const [left, right] = await Promise.all([
    getReportContent(pair.left, signal),
    getReportContent(pair.right, signal),
  ])
  return { left, right }
}
