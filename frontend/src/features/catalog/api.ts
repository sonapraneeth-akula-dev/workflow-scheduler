import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import type { components } from "@/lib/api/schema"
import { failureSummarySchema } from "@/features/runs/failures"
import { signOffStateSchema } from "@/features/runs/signoff-api"

export const runStatuses = [
  "Queued",
  "Running",
  "Aborting",
  "Succeeded",
  "Failed",
  "Aborted",
  "TimedOut",
] as const
export const statusSchema = z.enum([...runStatuses, "NeverRun"])
const name = z
  .string()
  .min(1)
  .regex(/^[^/\\\p{Cc}]+$/u)
const count = z.union([
  z.int().nonnegative(),
  z
    .string()
    .regex(/^(0|[1-9]\d*)$/)
    .transform(Number)
    .pipe(z.int().nonnegative()),
])
const displayName = z
  .string()
  .regex(/^[^\p{Cc}]+$/u)
  .refine(
    (value) => value.trim().length > 0 && Array.from(value).length <= 1024
  )
const presentationSchema = z.object({
  typeOrder: z.array(name).refine((keys) => new Set(keys).size === keys.length),
  groupingKeys: z
    .array(z.string().min(1))
    .refine((keys) => new Set(keys).size === keys.length),
  listColumns: z
    .array(z.string().min(1))
    .refine((keys) => new Set(keys).size === keys.length),
  defaultFilters: z.record(z.string().min(1), z.string()),
  labels: z.record(z.string().min(1), z.string().min(1)),
  names: z.record(displayName, displayName).optional(),
})
const teams = z.array(
  z.object({
    team: name,
    types: z.array(z.object({ team: name, name })),
    presentation: presentationSchema.nullable().optional(),
  })
)
const types = z.array(
  z.object({
    team: name,
    name,
    description: z.string(),
    metadataSchema: z.object({
      keys: z.array(
        z.object({
          key: z.string().min(1),
          required: z.boolean(),
          allowedValues: z.array(z.string()),
        })
      ),
      allowFreeForm: z.boolean(),
    }),
    latestStatusCounts: z.partialRecord(statusSchema, count),
  })
)
const version = z.object({ id: z.uuid(), semVer: z.string().min(1) })
export const catalogWorkflowSchema = z.object({
  id: z.string().min(1),
  team: name,
  type: name,
  name,
  activeVersion: version.nullable(),
  latestVersion: version,
  metadata: z.object({
    type: z.string(),
    values: z.record(z.string(), z.string()),
  }),
  latestRun: z
    .object({
      id: z.uuid(),
      status: z.enum(runStatuses),
      failureSummary: failureSummarySchema.nullish(),
      signOff: signOffStateSchema,
      createdAt: z.iso.datetime({ offset: true }),
      startedAt: z.iso.datetime({ offset: true }).nullable(),
      endedAt: z.iso.datetime({ offset: true }).nullable(),
    })
    .nullable(),
})
const workflowPage = z.object({
  items: z.array(catalogWorkflowSchema).max(50),
  nextCursor: z.string().min(1).nullable(),
})

export type CatalogType = z.infer<typeof types>[number]
export type CatalogWorkflow = z.infer<typeof catalogWorkflowSchema>
export type RunStatus = z.infer<typeof statusSchema>
export type Presentation = z.infer<typeof presentationSchema>

export async function getTeams(signal?: AbortSignal) {
  const result = await publicApi.GET("/api/v1/teams", { signal })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  const parsed = teams.parse(result.data)
  if (
    parsed.some((team) => team.types.some((type) => type.team !== team.team))
  ) {
    throw new Error("The catalogue returned a type outside its team.")
  }
  return parsed.map((team) => ({
    team: team.team,
    typeCount: team.types.length,
    presentation: team.presentation ?? null,
  }))
}

export async function getTypes(
  team: string,
  signal?: AbortSignal,
  metadata: Readonly<Record<string, string>> = {},
  defaults = false
) {
  const result = await publicApi.GET("/api/v1/teams/{team}/types", {
    params: {
      path: { team },
      query: {
        ...(defaults ? { defaults: true } : {}),
        ...Object.fromEntries(
          Object.entries(metadata).map(([key, value]) => [`meta.${key}`, value])
        ),
      },
    },
    signal,
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  const parsed = types.parse(result.data)
  if (parsed.some((type) => type.team !== team))
    throw new Error("The catalogue returned a different team's types.")
  return parsed
}

export async function getWorkflows(
  team: string,
  type: string,
  cursor: string | null,
  signal?: AbortSignal,
  metadata: Readonly<Record<string, string>> = {},
  defaults = false
) {
  const result = await publicApi.GET("/api/v1/workflows", {
    params: {
      query: {
        team,
        type,
        limit: 50,
        ...(cursor ? { cursor } : {}),
        ...(defaults ? { defaults: true } : {}),
        ...Object.fromEntries(
          Object.entries(metadata).map(([key, value]) => [`meta.${key}`, value])
        ),
      },
    },
    signal,
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  const parsed = workflowPage.parse(result.data)
  if (
    parsed.items.some(
      (item) =>
        item.team !== team ||
        item.type !== type ||
        item.id !== `${team}/${type}/${item.name}`
    )
  ) {
    throw new Error(
      "The catalogue returned workflows outside the requested type."
    )
  }
  return parsed satisfies components["schemas"]["CatalogWorkflowPage"]
}

export function accessDenied(error: unknown): boolean {
  return error instanceof ApiError && [401, 403, 404].includes(error.status)
}
