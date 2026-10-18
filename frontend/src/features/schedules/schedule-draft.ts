import { z } from "zod"
import type { Schedule, ScheduleRequest } from "@/lib/api/client"
import { JsonNumberSupportError, jsonObjectSchema, parseJson } from "@/lib/json"

export interface ScheduleDraft {
  readonly workflowId: string
  readonly name: string
  readonly timezone: string
  readonly expression: "cron" | "interval"
  readonly cron: string
  readonly seconds: string
  readonly versionPin: string
  readonly overlap: "Skip" | "Queue" | "Concurrent"
  readonly catchUp: "None" | "Latest"
  readonly variables: string
}

export const timezones = ["Etc/UTC", ...Intl.supportedValuesOf("timeZone")]
const variablesSchema = jsonObjectSchema

function validTimezone(value: string): boolean {
  if (!value.includes("/")) {
    return false
  }
  try {
    new Intl.DateTimeFormat("en", { timeZone: value }).format()
    return true
  } catch (error) {
    if (!(error instanceof RangeError)) {
      throw error
    }
    return false
  }
}

function validIdentity(value: string): boolean {
  const segments = value.split("/")
  return (
    segments.length === 3 &&
    segments.every(
      (segment) =>
        segment.trim().length > 0 &&
        segment !== "." &&
        segment !== ".." &&
        [...segment].length <= 128 &&
        !/[\p{Cc}\\]/u.test(segment)
    )
  )
}

export function durationSeconds(duration: string): string {
  const matched =
    /^(?:(\d+)\.)?([01]\d|2[0-3]):([0-5]\d):([0-5]\d)(?:\.0+)?$/.exec(duration)
  if (!matched) {
    throw new Error("The API returned an unsupported schedule interval.")
  }
  const seconds =
    Number(matched[1] ?? 0) * 86_400 +
    Number(matched[2]) * 3_600 +
    Number(matched[3]) * 60 +
    Number(matched[4])
  if (
    !Number.isSafeInteger(seconds) ||
    seconds < 1 ||
    seconds > 2_147_483_647
  ) {
    throw new Error("The API returned an out-of-range schedule interval.")
  }
  return String(seconds)
}

export function draftFor(schedule?: Schedule): ScheduleDraft {
  return {
    workflowId: schedule?.workflowId ?? "",
    name: schedule?.name ?? "",
    timezone: schedule?.timezone ?? "Etc/UTC",
    expression: schedule?.interval ? "interval" : "cron",
    cron: schedule?.cron ?? "0 9 * * 1-5",
    seconds: schedule?.interval ? durationSeconds(schedule.interval) : "60",
    versionPin: schedule?.versionPin ?? "",
    overlap: schedule?.overlap ?? "Skip",
    catchUp: schedule?.catchUp ?? "Latest",
    variables: JSON.stringify(schedule?.variables ?? {}, null, 2),
  }
}

export function validateDraft(draft: ScheduleDraft): {
  readonly request?: ScheduleRequest
  readonly errors: ReadonlyMap<keyof ScheduleDraft, string>
} {
  const errors = new Map<keyof ScheduleDraft, string>()
  if (!validIdentity(draft.workflowId)) {
    errors.set(
      "workflowId",
      "Use the exact team/type/workflow identity (1-128 characters per segment)."
    )
  }
  if (
    !draft.name.trim() ||
    [...draft.name].length > 128 ||
    /\p{Cc}/u.test(draft.name)
  ) {
    errors.set(
      "name",
      "Enter a name of 1-128 characters, without control characters."
    )
  }
  if (!validTimezone(draft.timezone)) {
    errors.set("timezone", "Choose an IANA timezone.")
  }
  if (
    draft.expression === "cron" &&
    draft.cron.trim().split(/\s+/).length !== 5
  ) {
    errors.set(
      "cron",
      "Use a standard five-field cron expression. Seconds fields are not supported."
    )
  }
  const seconds = Number(draft.seconds)
  if (
    draft.expression === "interval" &&
    (!/^\d+$/.test(draft.seconds) ||
      !Number.isSafeInteger(seconds) ||
      seconds < 1 ||
      seconds > 2_147_483_647)
  ) {
    errors.set(
      "seconds",
      "Enter a positive whole number of seconds, at most 2147483647."
    )
  }
  let variables: z.infer<typeof variablesSchema> = {}
  try {
    const json = parseJson(draft.variables)
    const parsed = variablesSchema.safeParse(json)
    if (parsed.success) {
      variables = parsed.data
    } else {
      errors.set(
        "variables",
        "Supply a JSON object mapping variable names to values."
      )
    }
  } catch (error) {
    if (error instanceof JsonNumberSupportError) {
      errors.set("variables", error.message)
    } else if (error instanceof SyntaxError) {
      errors.set("variables", "Supply a valid JSON object.")
    } else {
      throw error
    }
  }
  if (errors.size > 0) {
    return { errors }
  }
  const interval =
    `${Math.floor(seconds / 86_400)}.${String(Math.floor((seconds % 86_400) / 3_600)).padStart(2, "0")}:` +
    `${String(Math.floor((seconds % 3_600) / 60)).padStart(2, "0")}:${String(seconds % 60).padStart(2, "0")}`
  const request: ScheduleRequest = {
    workflowId: draft.workflowId,
    name: draft.name,
    timezone: draft.timezone,
    cron: draft.expression === "cron" ? draft.cron.trim() : null,
    interval: draft.expression === "interval" ? interval : null,
    versionPin: draft.versionPin.trim() || null,
    overlap: draft.overlap,
    catchUp: draft.catchUp,
    variables,
  }
  if (new TextEncoder().encode(JSON.stringify(request)).length > 1_048_576) {
    errors.set(
      "variables",
      "The schedule request exceeds 1 MiB. Reduce its variable payload."
    )
    return { errors }
  }
  return { request, errors }
}
