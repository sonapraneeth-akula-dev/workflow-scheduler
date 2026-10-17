import { z } from "zod"
import {
  JsonNumberSupportError,
  jsonValueSchema,
  parseJson,
  type JsonValue,
} from "@/lib/json"

const variableType = z.enum([
  "String",
  "Int",
  "Long",
  "Double",
  "Bool",
  "Enum",
  "StringList",
  "Duration",
  "Secret",
])
const variable = z.object({
  key: z.string().refine((key) => key.trim().length > 0 && !key.includes("\0")),
  type: variableType,
  required: z.boolean(),
  default: jsonValueSchema.nullable(),
  allowedValues: z.array(z.string()),
})
export type Variable = z.infer<typeof variable>
export type VariablesDraft = Readonly<Record<string, string | undefined>>

export function secretName(value: string): boolean {
  if (!value || value.length > 273) return false
  let name = value
  if (value.startsWith("secret://")) {
    const match = /^secret:\/\/([a-z](?:[a-z0-9-]{0,61}[a-z0-9])?)\/(.+)$/.exec(
      value
    )
    if (!match) return false
    name = match[2]!
  }
  return (
    name.length <= 200 &&
    name
      .split("/")
      .every(
        (part) =>
          part !== "." && part !== ".." && /^[A-Za-z0-9_.-]+$/.test(part)
      )
  )
}

function duration(value: string): boolean {
  const text = value.replace(/^[ \t]+|[ \t]+$/g, "")
  const daysOnly = /^(-)?(\d+)$/.exec(text)
  const time = /^(-)?(?:(\d+)\.)?(\d+):(\d+)(?::(\d*)(?:\.(\d{0,7}))?)?$/.exec(
    text
  )
  if ((!daysOnly && !time) || (time && time[5] === "" && time[6] === undefined))
    return false
  const hours = Number(time?.[3] ?? 0)
  const minutes = Number(time?.[4] ?? 0)
  const seconds = Number(time?.[5] ?? 0)
  const days = (time?.[2] ?? daysOnly?.[2] ?? "0").replace(/^0+/, "") || "0"
  if (days.length > 8 || hours > 23 || minutes > 59 || seconds > 59)
    return false
  const ticks =
    (BigInt(days) * 86400n +
      BigInt(hours) * 3600n +
      BigInt(minutes) * 60n +
      BigInt(seconds)) *
      10000000n +
    BigInt((time?.[6] ?? "").padEnd(7, "0"))
  return (
    ticks <=
    ((time?.[1] ?? daysOnly?.[1]) ? 9223372036854775808n : 9223372036854775807n)
  )
}

export function parseVariable(definition: Variable, text: string): JsonValue {
  if (text.includes("\0")) throw new Error("NUL characters are not allowed.")
  let value: JsonValue
  switch (definition.type) {
    case "String":
    case "Enum":
      value = text
      if (
        definition.allowedValues.length &&
        !definition.allowedValues.includes(text)
      )
        throw new Error("Choose one of the allowed values.")
      break
    case "Int":
    case "Long": {
      if (text.length > 21 || !/^-?(0|[1-9]\d*)$/.test(text))
        throw new Error(
          "Enter a JSON whole number, without decimals or an exponent."
        )
      const integer = BigInt(text)
      const min =
        definition.type === "Int" ? -2147483648n : -9223372036854775808n
      const max = definition.type === "Int" ? 2147483647n : 9223372036854775807n
      if (integer < min || integer > max)
        throw new Error(`Enter a whole number from ${min} to ${max}.`)
      value = jsonValueSchema.parse(parseJson(text))
      break
    }
    case "Double": {
      if (text.length > 1024) throw new Error("Enter a finite JSON number.")
      const parsed = parseJson(text)
      if (
        !(typeof parsed === "number" || JSON.isRawJSON?.(parsed)) ||
        !Number.isFinite(Number(text))
      )
        throw new Error("Enter a finite JSON number.")
      value = jsonValueSchema.parse(parsed)
      break
    }
    case "Bool":
      if (text !== "true" && text !== "false")
        throw new Error("Choose true or false.")
      value = text === "true"
      break
    case "StringList":
      value = z
        .array(z.string().refine((item) => !item.includes("\0")))
        .parse(parseJson(text))
      break
    case "Duration":
      if (!duration(text))
        throw new Error(
          "Use a .NET constant duration, [-][days.]hh:mm:ss[.fffffff], within TimeSpan limits."
        )
      value = text
      break
    case "Secret":
      if (!secretName(text))
        throw new Error(
          "Enter a safe secret name or secret://store/name, never a secret value."
        )
      value = { Name: text }
      break
  }
  return value
}

function textFor(definition: Variable): string | undefined {
  const value = definition.default
  if (value === null) return undefined
  switch (definition.type) {
    case "String":
    case "Enum":
    case "Duration":
      return z.string().parse(value)
    case "Secret":
      return z.strictObject({ Name: z.string() }).parse(value).Name
    default:
      return JSON.stringify(value)
  }
}

export const triggerSchema = z.object({
  workflowId: z.string().min(1),
  versionId: z.uuid(),
  version: z.string().min(1),
  variables: z.array(variable).superRefine((definitions, context) => {
    const keys = new Set<string>()
    for (const [index, definition] of definitions.entries()) {
      try {
        if (
          keys.has(definition.key) ||
          new Set(definition.allowedValues).size !==
            definition.allowedValues.length ||
          (definition.allowedValues.length > 0 &&
            !["String", "Enum"].includes(definition.type)) ||
          (definition.type === "Enum" && definition.allowedValues.length === 0)
        )
          throw new Error("Invalid variable schema.")
        keys.add(definition.key)
        const text = textFor(definition)
        if (text !== undefined) parseVariable(definition, text)
      } catch (error) {
        if (!(error instanceof Error)) throw error
        context.addIssue({
          code: "custom",
          path: [index],
          message: "The API returned an invalid variable schema or default.",
        })
      }
    }
  }),
})
export type TriggerSchema = z.infer<typeof triggerSchema>

export function variablesDraft(
  definitions: readonly Variable[]
): VariablesDraft {
  return Object.fromEntries(
    definitions.map((definition) => [definition.key, textFor(definition)])
  )
}

export function validateVariables(
  definitions: readonly Variable[],
  draft: VariablesDraft
): {
  variables: Record<string, JsonValue>
  errors: ReadonlyMap<string, string>
} {
  const errors = new Map<string, string>()
  const values: [string, JsonValue][] = []
  const keys = new Set(definitions.map((definition) => definition.key))
  for (const key of Object.keys(draft))
    if (!keys.has(key))
      errors.set(key, "This variable is not declared by the selected version.")
  for (const definition of definitions) {
    const text = Object.hasOwn(draft, definition.key)
      ? draft[definition.key]
      : undefined
    if (text === undefined) {
      if (definition.required && definition.default === null)
        errors.set(definition.key, "Supply this required variable.")
      continue
    }
    try {
      values.push([definition.key, parseVariable(definition, text)])
    } catch (error) {
      if (error instanceof JsonNumberSupportError)
        errors.set(definition.key, error.message)
      else if (error instanceof z.ZodError)
        errors.set(
          definition.key,
          "Supply a JSON array containing only strings, without NUL characters."
        )
      else if (error instanceof SyntaxError)
        errors.set(
          definition.key,
          "Supply valid JSON for this variable's type."
        )
      else if (error instanceof Error) errors.set(definition.key, error.message)
      else throw error
    }
  }
  return { variables: Object.fromEntries(values), errors }
}
