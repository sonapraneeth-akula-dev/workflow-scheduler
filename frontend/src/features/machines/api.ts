import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import type { components } from "@/lib/api/schema"
import { parseJson } from "@/lib/json"

const text = (max: number) =>
  z
    .string()
    .min(1)
    .max(max)
    .regex(/^[^\p{Cc}]+$/u)
    .refine((value) => value.trim().length > 0, "A nonblank value is required.")
const poolSchema = text(200)
const labelKey = text(100).refine(
  (key) => !key.includes("="),
  "Label keys cannot contain =."
)
const labelsSchema = z
  .record(labelKey, text(200))
  .refine(
    (labels) => Object.keys(labels).length <= 100,
    "At most 100 labels are allowed."
  )
export const machineMetadataSchema = z.object({
  pool: poolSchema,
  labels: labelsSchema,
})
export type MachineMetadata = z.infer<typeof machineMetadataSchema>
const idSchema = z
  .uuid()
  .refine((value) => value !== "00000000-0000-0000-0000-000000000000")
const timestamp = z.iso.datetime({ offset: true })
const machineSchema = machineMetadataSchema.extend({
  id: idSchema,
  name: z
    .string()
    .min(1)
    .max(200)
    .refine((value) => value.trim().length > 0),
  os: z.enum(["windows", "linux"]),
  arch: z.enum(["x64", "arm64"]),
  cores: z.int().positive(),
  ramMb: z.int().positive(),
  maxTasks: z.int().positive(),
  slotsUsed: z.int().nonnegative(),
  status: z.enum(["Healthy", "Unhealthy", "Draining"]),
  lastHeartbeatAt: timestamp,
  version: z
    .string()
    .min(1)
    .max(100)
    .refine((value) => value.trim().length > 0),
  cpuPercent: z.number().finite().min(0).max(100),
  ramUsedMb: z.int().nonnegative(),
  diskFreeMb: z.int().nonnegative(),
  currentReservation: z
    .object({
      id: idSchema,
      runId: idSchema,
      nodeKey: z.string().min(1),
      epoch: z.int().positive(),
      expiresAt: timestamp,
      releasePending: z.boolean(),
    })
    .nullable(),
})
export type Machine = z.infer<typeof machineSchema>

export function parseLabels(input: string): Record<string, string> {
  if (input === "") return {}
  const entries = input.split(/\r?\n/).map((line) => {
    const equal = line.indexOf("=")
    if (equal < 1) throw new Error("Use one distinct key=value label per line.")
    return [line.slice(0, equal), line.slice(equal + 1)] as const
  })
  if (new Set(entries.map(([key]) => key)).size !== entries.length)
    throw new Error("Label keys must be unique.")
  return labelsSchema.parse(Object.fromEntries(entries))
}
export function formatLabels(labels: Readonly<Record<string, string>>): string {
  return Object.entries(labels)
    .sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))
    .map(([key, value]) => `${key}=${value}`)
    .join("\n")
}
export const machineSearchSchema = z
  .object({
    pool: poolSchema.optional(),
    os: z.enum(["windows", "linux"]).optional(),
    status: z.enum(["Healthy", "Unhealthy", "Draining"]).optional(),
    label: z.array(z.string()).max(100).optional(),
  })
  .superRefine((value, context) => {
    try {
      parseLabels(value.label?.join("\n") ?? "")
    } catch (error) {
      context.addIssue({
        code: "custom",
        path: ["label"],
        message: error instanceof Error ? error.message : "Invalid labels.",
      })
    }
  })
export type MachineSearch = z.infer<typeof machineSearchSchema>

export async function getMachines(
  search: MachineSearch,
  signal?: AbortSignal
): Promise<Machine[]> {
  const filters = machineSearchSchema.parse(search)
  const labels = Object.entries(parseLabels(filters.label?.join("\n") ?? ""))
  const result = await publicApi.GET("/api/v1/machines", {
    params: { query: filters },
    signal,
    parseAs: "text",
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  if (typeof result.data !== "string")
    throw new Error("The API returned an empty machine inventory.")
  const page = z
    .object({ items: z.array(machineSchema) })
    .parse(parseJson(result.data))
  if (
    new Set(page.items.map((row) => row.id)).size !== page.items.length ||
    page.items.some(
      (row) =>
        (filters.pool !== undefined && row.pool !== filters.pool) ||
        (filters.os !== undefined && row.os !== filters.os) ||
        (filters.status !== undefined && row.status !== filters.status) ||
        labels.some(([key, value]) => row.labels[key] !== value)
    )
  )
    throw new Error(
      "The API returned inconsistent machine inventory for these filters."
    )
  page satisfies components["schemas"]["MachineListResponse"]
  return page.items
}
export async function patchMachine(
  id: string,
  patch: Partial<MachineMetadata>
): Promise<void> {
  idSchema.parse(id)
  const body = machineMetadataSchema
    .partial()
    .strict()
    .refine(
      (value) => value.pool !== undefined || value.labels !== undefined,
      "Change labels or pool before saving."
    )
    .parse(patch)
  const result = await publicApi.PATCH("/api/v1/machines/{id}", {
    params: { path: { id } },
    body,
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  if (result.response.status !== 204)
    throw new Error("The API did not confirm the machine update.")
}
