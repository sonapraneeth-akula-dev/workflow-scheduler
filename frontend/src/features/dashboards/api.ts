import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import type { components } from "@/lib/api/schema"
import { runFilterSchema } from "@/features/runs/search"
import { trendWindowSchema, type TrendWindow } from "@/features/series/api"

export const dashboardScopeSchema = z.enum([
  "workflow",
  "schedule",
  "node",
  "machine",
])
export type DashboardScope = z.infer<typeof dashboardScopeSchema>
const nodeKey = z
  .string()
  .min(1)
  .max(256)
  .regex(/^[^/\\\p{Cc}]+$/u)
  .refine(
    (value) =>
      value.trim().length > 0 &&
      value !== "." &&
      value !== ".." &&
      [...value].length <= 128
  )
const workflowId = runFilterSchema.shape.workflowId
  .unwrap()
  .refine((value) =>
    value.split("/").every((segment) => nodeKey.safeParse(segment).success)
  )
const uuid = z
  .uuid()
  .refine(
    (value) =>
      value === value.toLowerCase() &&
      value !== "00000000-0000-0000-0000-000000000000"
  )

export function encodeDashboardId(identity: string): string {
  return btoa(String.fromCharCode(...new TextEncoder().encode(identity)))
    .replaceAll("+", "-")
    .replaceAll("/", "_")
    .replace(/=+$/, "")
}

function decodeDashboardId(id: string): string {
  return new TextDecoder("utf-8", { fatal: true }).decode(
    Uint8Array.from(
      atob(id.replaceAll("-", "+").replaceAll("_", "/")),
      (character) => character.charCodeAt(0)
    )
  )
}

export const dashboardResourceSchema = z
  .strictObject({
    scope: dashboardScopeSchema,
    id: z.string().min(1).max(4096),
  })
  .superRefine(({ scope, id }, context) => {
    let valid = false
    if (scope === "schedule" || scope === "machine")
      valid = uuid.safeParse(id).success
    else {
      try {
        const identity = decodeDashboardId(id)
        const parts = identity.split("/")
        valid =
          encodeDashboardId(identity) === id &&
          parts.length === (scope === "node" ? 4 : 3) &&
          workflowId.safeParse(parts.slice(0, 3).join("/")).success &&
          (scope !== "node" || nodeKey.safeParse(parts[3]).success)
      } catch (error) {
        if (!(error instanceof TypeError || error instanceof DOMException))
          throw error
      }
    }
    if (!valid)
      context.addIssue({
        code: "custom",
        path: ["id"],
        message: "Use a canonical UUID or base64url UTF8 dashboard identity.",
      })
  })
export type DashboardResource = z.infer<typeof dashboardResourceSchema>
export const dashboardSearchSchema = z.object({
  window: trendWindowSchema.optional(),
  top: z.coerce.number().int().min(1).max(50).optional(),
})
const count = z.number().int().nonnegative().max(Number.MAX_SAFE_INTEGER)
const measurement = z.number().finite().nonnegative().nullable()
const rowSchema = z
  .strictObject({
    workflowId,
    nodeKey,
    observations: count.positive(),
    failures: count,
    softFailures: count,
    durationCount: count,
    failureRate: z.number().min(0).max(1),
    p50Ms: measurement,
    p95Ms: measurement,
    cpuMaxPercent: measurement,
    ramPeakMb: measurement,
    latestRunId: uuid,
    runHref: z.string(),
  })
  .refine(
    (row) =>
      row.failures + row.softFailures <= row.observations &&
      row.durationCount <= row.observations &&
      Math.abs(row.failureRate - row.failures / row.observations) < 1e-12 &&
      (row.durationCount === 0
        ? row.p50Ms === null && row.p95Ms === null
        : row.p50Ms !== null && row.p95Ms !== null && row.p95Ms >= row.p50Ms) &&
      row.runHref === `/runs/${row.latestRunId}`,
    "Dashboard counts, measurements and contributing-run link must agree."
  )
export type DashboardRow = z.infer<typeof rowSchema>
const list = z.array(rowSchema).max(50)
export const dashboardSchema = z.strictObject({
  scope: dashboardScopeSchema,
  id: z.string(),
  window: trendWindowSchema,
  from: z.iso.date(),
  to: z.iso.date(),
  top: z.number().int().min(1).max(50),
  observationKind: z.enum(["Node", "TaskAttempt"]),
  rankings: z.strictObject({
    slowest: list,
    failureRate: list,
    cpu: list,
    ram: list,
  }),
})
export type Dashboard = z.infer<typeof dashboardSchema>
const metrics = {
  slowest: "p95Ms",
  failureRate: "failureRate",
  cpu: "cpuMaxPercent",
  ram: "ramPeakMb",
} as const

export async function getDashboard(
  scope: DashboardScope,
  id: string,
  window: TrendWindow = "30d",
  top = 10,
  signal?: AbortSignal
) {
  const resource = dashboardResourceSchema.parse({ scope, id })
  const search = dashboardSearchSchema.parse({ window, top })
  const result = await publicApi.GET("/api/v1/dashboards/{scope}/{id}", {
    params: { path: resource, query: search },
    signal,
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  const data = dashboardSchema.parse(result.data)
  if (
    data.scope !== scope ||
    data.id !== id ||
    data.window !== search.window ||
    data.top !== search.top ||
    data.observationKind !== (scope === "machine" ? "TaskAttempt" : "Node") ||
    Date.parse(data.to) - Date.parse(data.from) !==
      Number(data.window.slice(0, -1)) * 86400000 ||
    (data.rankings.failureRate.length === 0 &&
      [data.rankings.slowest, data.rankings.cpu, data.rankings.ram].some(
        (rows) => rows.length > 0
      ))
  )
    throw new Error("The API returned a different dashboard scope or window.")
  const identity =
    scope === "workflow" || scope === "node"
      ? decodeDashboardId(id).split("/")
      : undefined
  const seen = new Map<string, string>()
  for (const [ranking, metric] of Object.entries(metrics)) {
    const rows = data.rankings[ranking as keyof typeof metrics]
    const unique = new Set<string>()
    if (
      rows.length > top ||
      rows.some((row, index) => {
        const key = `${row.workflowId}/${row.nodeKey}`
        const previous = rows[index - 1]
        const value = row[metric]
        const other = seen.get(key)
        const invalid =
          unique.has(key) ||
          (other !== undefined && other !== JSON.stringify(row)) ||
          (identity !== undefined &&
            (row.workflowId !== identity.slice(0, 3).join("/") ||
              (scope === "node" && row.nodeKey !== identity[3]))) ||
          value === null ||
          (previous !== undefined &&
            (value > previous[metric]! ||
              (value === previous[metric] &&
                (row.workflowId < previous.workflowId ||
                  (row.workflowId === previous.workflowId &&
                    row.nodeKey < previous.nodeKey)))))
        unique.add(key)
        seen.set(key, JSON.stringify(row))
        return invalid
      })
    )
      throw new Error("The API returned inconsistent dashboard rankings.")
  }
  return data satisfies components["schemas"]["DashboardResponse"]
}
