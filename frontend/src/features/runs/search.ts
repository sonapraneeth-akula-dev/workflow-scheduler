import { z } from "zod"
import { runStatuses } from "@/features/catalog/api"

const name = z
  .string()
  .min(1)
  .max(256)
  .regex(/^[^/\\\p{Cc}]+$/u)
export const instantSchema = z.iso
  .datetime({ offset: true })
  .refine(
    (value) => !/\.\d{7}/.test(value) && Number.isFinite(Date.parse(value)),
    "Use an RFC3339 timestamp with an explicit offset and at most six fractional digits."
  )
export const metadataValuesSchema = z
  .custom<Record<string, unknown>>(
    (value) =>
      typeof value === "object" &&
      value !== null &&
      !Array.isArray(value) &&
      (Object.getPrototypeOf(value) === Object.prototype ||
        Object.getPrototypeOf(value) === null),
    "Use a JSON object of string metadata values."
  )
  .transform((value) => Object.entries(value))
  .pipe(z.array(z.tuple([z.string(), z.string()])))
  .transform((entries) => Object.fromEntries(entries))
const metadata = metadataValuesSchema
  .refine(
    (value) =>
      Object.entries(value).every(
        ([key, value]) =>
          key.length >= 1 && key.length <= 128 && value.length <= 1024
      ),
    "Metadata keys require 1-128 characters and values at most 1024 characters."
  )
  .refine(
    (value) => Object.keys(value).length <= 32,
    "Use at most 32 metadata filters."
  )

export function instantMicros(value: string): bigint {
  const fraction = /\.(\d+)/.exec(value)?.[1] ?? ""
  return (
    BigInt(Date.parse(value)) * 1000n + BigInt(fraction.padEnd(6, "0").slice(3))
  )
}

export const runFilterSchema = z.object({
  team: name.optional(),
  type: name.optional(),
  workflowId: z
    .string()
    .max(1024)
    .regex(/^[^/\\\p{Cc}]+\/[^/\\\p{Cc}]+\/[^/\\\p{Cc}]+$/u)
    .optional(),
  seriesId: z.uuid().optional(),
  status: z.enum(runStatuses).optional(),
  from: instantSchema.optional(),
  to: instantSchema.optional(),
  metadata: metadata.optional(),
})
export const runSearchSchema = runFilterSchema
  .extend({
    groupBy: z
      .enum(["none", "team", "type", "workflow", "status", "metadata"])
      .optional(),
    groupKey: z.string().min(1).max(128).optional(),
  })
  .superRefine((filters, context) => {
    if (filters.type && !filters.team)
      context.addIssue({
        code: "custom",
        path: ["type"],
        message: "A type filter requires a team.",
      })
    if (
      filters.from &&
      filters.to &&
      instantMicros(filters.from) >= instantMicros(filters.to)
    )
      context.addIssue({
        code: "custom",
        path: ["to"],
        message: "The exclusive end must be after the inclusive start.",
      })
    if (filters.groupBy === "metadata" && !filters.groupKey)
      context.addIssue({
        code: "custom",
        path: ["groupKey"],
        message: "Choose a metadata key to group by.",
      })
    if (filters.workflowId) {
      const [team, type] = filters.workflowId.split("/")
      if (
        (filters.team && team !== filters.team) ||
        (filters.type && type !== filters.type)
      )
        context.addIssue({
          code: "custom",
          path: ["workflowId"],
          message: "The workflow must belong to the filtered team and type.",
        })
    }
  })

export type RunFilters = z.infer<typeof runFilterSchema>
export type RunSearch = z.infer<typeof runSearchSchema>
