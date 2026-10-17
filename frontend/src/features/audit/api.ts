import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import type { components } from "@/lib/api/schema"
import { jsonObjectSchema, parseJson } from "@/lib/json"
import { instantMicros, instantSchema } from "@/features/runs/search"

const text = z
  .string()
  .min(1)
  .max(1024)
  .regex(/^[^\p{Cc}]+$/u)
  .refine((value) => value.trim().length > 0)
export const auditSearchSchema = z
  .object({
    actor: text.optional(),
    targetKind: text.optional(),
    targetId: text.optional(),
    from: instantSchema.optional(),
    to: instantSchema.optional(),
  })
  .superRefine((value, context) => {
    if (
      value.from &&
      value.to &&
      instantMicros(value.from) >= instantMicros(value.to)
    )
      context.addIssue({
        code: "custom",
        path: ["to"],
        message: "The exclusive end must be after the inclusive start.",
      })
  })
export type AuditSearch = z.infer<typeof auditSearchSchema>
const rowSchema = z.object({
  id: z
    .string()
    .regex(/^[1-9][0-9]{0,18}$/)
    .refine((value) => BigInt(value) <= 9223372036854775807n),
  at: instantSchema,
  actor: text,
  actorKind: z.enum(["User", "App"]),
  action: text,
  targetKind: text,
  targetId: text,
  teamId: text.nullable(),
  summary: jsonObjectSchema,
})
export type AuditRow = z.infer<typeof rowSchema>
const pageSchema = z.object({
  items: z.array(rowSchema).max(50),
  nextCursor: z
    .string()
    .min(100)
    .max(256)
    .regex(/^[A-Za-z0-9_-]+$/)
    .nullable(),
})
export type AuditCursor = { cursor: string; after: AuditRow } | null

export async function getAudit(
  search: AuditSearch,
  cursor: AuditCursor,
  signal?: AbortSignal
) {
  const filters = auditSearchSchema.parse(search)
  const result = await publicApi.GET("/api/v1/audit", {
    params: { query: { ...filters, limit: 50, cursor: cursor?.cursor } },
    signal,
    parseAs: "text",
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  if (typeof result.data !== "string")
    throw new Error("The API returned an empty audit response.")
  const page = pageSchema.parse(parseJson(result.data))
  if (
    page.nextCursor === cursor?.cursor ||
    (page.nextCursor !== null && page.items.length !== 50) ||
    page.items.some((row, index) => {
      const previous = index ? page.items[index - 1]! : cursor?.after
      const at = instantMicros(row.at)
      return (
        (filters.actor !== undefined && row.actor !== filters.actor) ||
        (filters.targetKind !== undefined &&
          row.targetKind !== filters.targetKind) ||
        (filters.targetId !== undefined && row.targetId !== filters.targetId) ||
        (filters.from !== undefined && at < instantMicros(filters.from)) ||
        (filters.to !== undefined && at >= instantMicros(filters.to)) ||
        (previous !== undefined &&
          (at > instantMicros(previous.at) ||
            (at === instantMicros(previous.at) &&
              BigInt(row.id) >= BigInt(previous.id))))
      )
    })
  )
    throw new Error(
      "The API returned inconsistent audit history for these filters."
    )
  return page satisfies components["schemas"]["AuditPage"]
}
