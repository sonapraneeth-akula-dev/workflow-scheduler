import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import { parseJson, type RawJson } from "@/lib/json"

// Integer counters remain exact strings; only chart coordinates become approximate numbers.
const rawNumber = z.custom<RawJson>(
  (value) => typeof JSON.isRawJSON === "function" && JSON.isRawJSON(value)
)
const integer = z
  .union([
    z.number().int().nonnegative().max(Number.MAX_SAFE_INTEGER),
    rawNumber,
  ])
  .transform((value, context) => {
    const text = JSON.stringify(value)
    if (!/^(0|[1-9]\d*)$/.test(text)) {
      context.addIssue({
        code: "custom",
        message: "An exact unsigned JSON integer is required.",
      })
      return z.NEVER
    }
    return text
  })
const uint64 = integer.refine((value) => BigInt(value) <= 18446744073709551615n)
const finite = z
  .union([z.number(), rawNumber])
  .transform((value) =>
    typeof value === "number" ? value : Number(value.rawJSON)
  )
  .pipe(z.number().finite().nonnegative())
const reason = z
  .string()
  .min(1)
  .max(512)
  .regex(/^[^\p{Cc}]+$/u)
  .nullable()
const taskKey = z
  .string()
  .refine(
    (key) =>
      key.split("/").length === 2 &&
      key.split("/").every((part) => part.trim().length > 0) &&
      new TextEncoder().encode(key).length <= 1025
  )
const runIdSchema = z
  .uuid()
  .refine((id) => id[14] === "7" && id === id.toLowerCase())
const attemptSchema = z.int().min(1).max(32767)
const cursorSchema = z
  .string()
  .min(1)
  .max(2048)
  .regex(/^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/)
export const resourcePageSize = 60

const pointSchema = z
  .object({
    atUtc: z.iso.datetime({ offset: true }),
    intervalSeconds: finite.refine((value) => value > 0).nullable(),
    cpuPercent: finite.nullable(),
    ramBytes: uint64.nullable(),
    networkSentBytes: uint64.nullable(),
    networkReceivedBytes: uint64.nullable(),
    networkSentDeltaBytes: uint64.nullable(),
    networkReceivedDeltaBytes: uint64.nullable(),
    networkSentBytesPerSecond: finite.nullable(),
    networkReceivedBytesPerSecond: finite.nullable(),
    cpuScope: z.enum([
      "unavailable",
      "process",
      "job-with-descendants",
      "cgroup-with-descendants",
      "machine",
    ]),
    memoryScope: z.enum([
      "unavailable",
      "process-rss",
      "job-resident-sum",
      "cgroup-memory.current",
      "physical-used",
    ]),
    networkScope: z.enum([
      "unavailable",
      "host-process-etw-transport",
      "cgroup-skb-l3-with-descendants",
      "non-loopback-interfaces",
    ]),
    cpuUnavailableReason: reason,
    memoryUnavailableReason: reason,
    networkUnavailableReason: reason,
    networkRateUnavailableReason: reason,
  })
  .superRefine((point, context) => {
    const invalid = [
      [point.cpuPercent, point.cpuUnavailableReason],
      [point.ramBytes, point.memoryUnavailableReason],
      [point.networkSentBytes, point.networkUnavailableReason],
      [point.networkSentBytesPerSecond, point.networkRateUnavailableReason],
    ].some(([value, why]) => (value === null) === (why === null))
    if (
      invalid ||
      (point.networkSentBytes === null) !==
        (point.networkReceivedBytes === null) ||
      (point.networkSentBytesPerSecond === null) !==
        (point.networkReceivedBytesPerSecond === null) ||
      (point.networkSentDeltaBytes === null) !==
        (point.networkReceivedDeltaBytes === null) ||
      (point.networkSentDeltaBytes === null) !==
        (point.networkSentBytesPerSecond === null) ||
      (point.cpuPercent !== null &&
        (point.intervalSeconds === null || point.cpuScope === "unavailable")) ||
      (point.ramBytes !== null && point.memoryScope === "unavailable") ||
      (point.networkSentBytes !== null &&
        point.networkScope === "unavailable") ||
      (point.networkSentDeltaBytes !== null &&
        (point.intervalSeconds === null || point.networkSentBytes === null))
    )
      context.addIssue({
        code: "custom",
        message: "Resource values and availability metadata disagree.",
      })
  })
export type ResourcePoint = z.infer<typeof pointSchema>
const observationSchema = z
  .object({
    task: pointSchema,
    machine: pointSchema.nullable(),
    machineUnavailableReason: reason,
  })
  .superRefine((observation, context) => {
    const machine = observation.machine
    if (
      (machine === null) === (observation.machineUnavailableReason === null) ||
      observation.task.cpuScope === "machine" ||
      observation.task.memoryScope === "physical-used" ||
      observation.task.networkScope === "non-loopback-interfaces" ||
      (machine &&
        (machine.cpuScope !== "machine" ||
          machine.memoryScope !== "physical-used" ||
          !["unavailable", "non-loopback-interfaces"].includes(
            machine.networkScope
          ) ||
          (machine.cpuPercent !== null && machine.cpuPercent > 100)))
    )
      context.addIssue({
        code: "custom",
        message: "Task and machine resource scopes disagree.",
      })
  })
export const resourcePageSchema = z
  .object({
    samples: z
      .array(
        z.object({
          sequence: uint64.refine((value) => value !== "0"),
          observation: observationSchema,
        })
      )
      .max(resourcePageSize),
    nextCursor: cursorSchema.nullable(),
  })
  .superRefine((page, context) => {
    if (
      (page.samples.length === 0 && page.nextCursor !== null) ||
      page.samples.some(
        (sample, index) =>
          index > 0 &&
          BigInt(page.samples[index - 1]!.sequence) !==
            BigInt(sample.sequence) + 1n
      )
    )
      context.addIssue({
        code: "custom",
        message: "The API returned an unordered or incomplete resource page.",
      })
  })
export type ResourcePage = z.infer<typeof resourcePageSchema>
export const resourceSummarySchema = z
  .array(
    z.object({
      taskKey,
      attempt: attemptSchema,
      agentId: z.uuid().nullable(),
      sampleCount: integer.refine(
        (value) => BigInt(value) <= 9223372036854775807n
      ),
      averageCpuPercent: finite.nullable(),
      maxCpuPercent: finite.nullable(),
      peakRamBytes: integer.nullable(),
      networkSentBytes: integer.nullable(),
      networkReceivedBytes: integer.nullable(),
      cpuObservedSeconds: finite,
      networkObservedSeconds: finite,
      cpuUnavailableReason: reason,
      memoryUnavailableReason: reason,
      networkUnavailableReason: reason,
    })
  )
  .superRefine((items, context) => {
    const keys = new Set<string>()
    for (const item of items) {
      const key = JSON.stringify([item.taskKey, item.attempt])
      if (
        keys.has(key) ||
        (item.averageCpuPercent === null) !== (item.maxCpuPercent === null) ||
        (item.networkSentBytes === null) !==
          (item.networkReceivedBytes === null) ||
        [
          [item.averageCpuPercent, item.cpuUnavailableReason],
          [item.peakRamBytes, item.memoryUnavailableReason],
          [item.networkSentBytes, item.networkUnavailableReason],
        ].some(([value, why]) => (value === null) === (why === null))
      )
        context.addIssue({
          code: "custom",
          message: "The API returned inconsistent resource summaries.",
        })
      keys.add(key)
    }
  })
export type ResourceSummary = z.infer<typeof resourceSummarySchema>[number]
export interface ResourceCursor {
  readonly token: string
  readonly before: string
}

function responseJson(data: unknown): unknown {
  if (typeof data !== "string")
    throw new Error("The API returned an empty resource response.")
  return parseJson(data)
}

export async function getResourceSummary(runId: string, signal?: AbortSignal) {
  const result = await publicApi.GET("/api/v1/runs/{id}/resource-metrics", {
    params: { path: { id: runIdSchema.parse(runId) } },
    signal,
    parseAs: "text",
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  return resourceSummarySchema.parse(responseJson(result.data))
}

export async function getResourcePage(
  runId: string,
  selected: Pick<ResourceSummary, "taskKey" | "attempt">,
  cursor: ResourceCursor | null,
  signal?: AbortSignal
): Promise<ResourcePage> {
  const result = await publicApi.GET("/api/v1/runs/{id}/resources", {
    params: {
      path: { id: runIdSchema.parse(runId) },
      query: {
        taskKey: taskKey.parse(selected.taskKey),
        attempt: attemptSchema.parse(selected.attempt),
        limit: resourcePageSize,
        ...(cursor ? { cursor: cursorSchema.parse(cursor.token) } : {}),
      },
    },
    signal,
    parseAs: "text",
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  const page = resourcePageSchema.parse(responseJson(result.data))
  if (
    cursor &&
    page.samples[0] &&
    BigInt(page.samples[0].sequence) !== BigInt(cursor.before) - 1n
  )
    throw new Error(
      "The API returned resource samples outside the requested page."
    )
  return page
}
