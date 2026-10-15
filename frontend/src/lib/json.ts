import { z } from "zod"

export interface RawJson {
  readonly rawJSON: string
}

declare global {
  interface JSON {
    rawJSON(source: string): RawJson
    isRawJSON(value: unknown): value is RawJson
  }
}

export class JsonNumberSupportError extends Error {
  constructor() {
    super(
      "This browser cannot preserve large JSON numbers. Use a current browser before editing this schedule."
    )
  }
}

export function parseJson(text: string): unknown {
  return JSON.parse(
    text,
    (_key: string, value: unknown, context?: { source?: string }) => {
      if (
        typeof value === "number" &&
        (!Number.isFinite(value) ||
          (Number.isInteger(value) && !Number.isSafeInteger(value)))
      ) {
        if (
          !context?.source ||
          typeof JSON.rawJSON !== "function" ||
          typeof JSON.isRawJSON !== "function"
        ) {
          throw new JsonNumberSupportError()
        }
        // Keep Int64 and large double tokens intact through parse, validation and stringify.
        return JSON.rawJSON(context.source)
      }
      return value
    }
  )
}

export type JsonValue =
  | null
  | string
  | number
  | boolean
  | RawJson
  | JsonValue[]
  | { [key: string]: JsonValue }

export const jsonValueSchema: z.ZodType<JsonValue> = z.lazy(() =>
  z.union([
    z.custom<RawJson>(
      (value) => typeof JSON.isRawJSON === "function" && JSON.isRawJSON(value)
    ),
    z.null(),
    z.string(),
    z.number(),
    z.boolean(),
    z.array(jsonValueSchema),
    z.record(z.string(), jsonValueSchema),
  ])
)
export const jsonObjectSchema = z.record(z.string(), jsonValueSchema)
