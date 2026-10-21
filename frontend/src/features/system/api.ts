import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import type { components } from "@/lib/api/schema"
import { parseJson } from "@/lib/json"
import { accessToken } from "@/lib/auth/session"

const label = z
  .string()
  .min(1)
  .max(200)
  .regex(/^[^\p{Cc}]+$/u)
const count = z.int().nonnegative()
const timestamp = z.iso.datetime({ offset: true })
const instanceId = z.string().regex(/^[0-9a-f]{32}$/)
const instance = z
  .object({
    id: instanceId,
    name: label,
    hostName: label,
    nodeName: label,
    podName: label.nullable(),
    namespace: label.nullable(),
    role: z.enum(["Api", "ApiController"]),
    version: z.string().min(1).max(100),
    status: z.enum(["Healthy", "Unhealthy", "Draining", "Stopped"]),
    isLeader: z.boolean(),
    startedAt: timestamp,
    lastHeartbeatAt: timestamp,
    httpRequests: count,
    liveConnections: count,
    liveSubscriptions: count,
    liveDeliveries: count,
  })
  .refine(
    (row) =>
      !row.isLeader ||
      (row.role === "ApiController" && row.status === "Healthy"),
    "Only a healthy controller can hold the active lease."
  )
const worker = z
  .object({
    id: z.uuid(),
    name: label,
    pool: label,
    status: z.enum(["Healthy", "Unhealthy", "Draining"]),
    lastHeartbeatAt: timestamp,
    maxTasks: z.int().positive(),
    slotsUsed: count,
    assignedTasks: count,
    runningTasks: count,
    activeDagNodes: count,
    activeRuns: count,
    activeWorkflows: count,
  })
  .refine(
    (row) =>
      row.slotsUsed <= row.maxTasks &&
      row.runningTasks <= row.assignedTasks &&
      row.activeDagNodes <= row.assignedTasks &&
      row.activeRuns <= row.assignedTasks &&
      row.activeWorkflows <= row.activeRuns,
    "Worker counts are inconsistent."
  )
const totals = z
  .object({
    queuedRuns: count,
    runningRuns: count,
    abortingRuns: count,
    activeWorkflows: count,
    assignedTasks: count,
    runningTasks: count,
    activeDagNodes: count,
  })
  .refine(
    (row) =>
      row.runningTasks <= row.assignedTasks &&
      row.activeDagNodes <= row.assignedTasks,
    "System totals are inconsistent."
  )
const snapshotSchema = z
  .object({
    observedAt: timestamp,
    heartbeatTimeoutSeconds: z.int().positive(),
    instances: z.array(instance),
    workers: z.array(worker),
    totals,
  })
  .refine(
    (snapshot) =>
      new Set(snapshot.instances.map((row) => row.id)).size ===
        snapshot.instances.length &&
      new Set(snapshot.workers.map((row) => row.id)).size ===
        snapshot.workers.length &&
      snapshot.instances.filter((row) => row.isLeader).length <= 1,
    "System inventory contains duplicate identities or multiple active leaders."
  ) satisfies z.ZodType<components["schemas"]["SystemSnapshotResponse"]>

export type SystemSnapshot = z.infer<typeof snapshotSchema>
const memorySample = {
  workingSetBytes: count.nullable(),
  sampledAt: timestamp.nullable(),
}
const pairedSample = (row: {
  workingSetBytes: number | null
  sampledAt: string | null
}) => (row.workingSetBytes === null) === (row.sampledAt === null)
const kubernetesMemorySchema = z.object({
  observedAt: timestamp,
  staleAfterSeconds: z.int().positive(),
  nodes: z.array(
    z
      .object({
        name: label,
        capacityBytes: z.int().positive(),
        allocatableBytes: count,
        ...memorySample,
      })
      .refine(pairedSample, "RAM and sample time must be present together.")
  ),
  pods: z.array(
    z
      .object({
        name: label,
        namespace: label,
        uid: label,
        nodeName: label,
        role: z.enum([
          "kardia-api",
          "kardia-controller",
          "kardia-frontend",
          "kardia-worker",
        ]),
        phase: z.enum(["Pending", "Running", "Succeeded", "Failed", "Unknown"]),
        ready: z.boolean(),
        ...memorySample,
      })
      .refine(pairedSample, "RAM and sample time must be present together.")
  ),
}) satisfies z.ZodType<components["schemas"]["KubernetesMemorySnapshot"]>
export type KubernetesMemorySnapshot = z.infer<typeof kubernetesMemorySchema>

export async function getKubernetesMemory(
  signal?: AbortSignal
): Promise<KubernetesMemorySnapshot> {
  const token = await accessToken()
  const response = await fetch("/api/v1/admin/system/kubernetes-memory", {
    signal,
    cache: "no-store",
    headers: token ? { Authorization: `Bearer ${token}` } : undefined,
  })
  const text = await response.text()
  if (!response.ok)
    throw new ApiError(
      response,
      text.startsWith("{") ? parseJson(text) : undefined
    )
  return kubernetesMemorySchema.parse(parseJson(text))
}
export interface SystemObservation {
  snapshot: SystemSnapshot
  respondingInstanceId: string | null
}
export interface SystemFrame extends SystemObservation {
  dispatchWorkers: string[]
  heartbeatWorkers: string[]
  messageInstances: string[]
}

export async function getSystem(
  signal?: AbortSignal
): Promise<SystemObservation> {
  const result = await publicApi.GET("/api/v1/admin/system", {
    signal,
    parseAs: "text",
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  if (typeof result.data !== "string")
    throw new Error("The API returned an empty system inventory.")
  return {
    snapshot: snapshotSchema.parse(parseJson(result.data)),
    respondingInstanceId: instanceId
      .nullable()
      .parse(result.response.headers.get("X-Kardia-Instance")),
  }
}

export function systemFrame(
  current: SystemObservation,
  previous?: SystemObservation
): SystemFrame {
  const workers = new Map(
    previous?.snapshot.workers.map((row) => [row.id, row])
  )
  const instances = new Map(
    previous?.snapshot.instances.map((row) => [row.id, row])
  )
  return {
    ...current,
    dispatchWorkers: current.snapshot.workers
      .filter((row) => {
        const prior = workers.get(row.id)
        return prior !== undefined && row.assignedTasks > prior.assignedTasks
      })
      .map((row) => row.id),
    heartbeatWorkers: current.snapshot.workers
      .filter((row) => {
        const prior = workers.get(row.id)
        return (
          prior !== undefined &&
          Date.parse(row.lastHeartbeatAt) > Date.parse(prior.lastHeartbeatAt)
        )
      })
      .map((row) => row.id),
    messageInstances: current.snapshot.instances
      .filter((row) => {
        const prior = instances.get(row.id)
        return prior !== undefined && row.liveDeliveries > prior.liveDeliveries
      })
      .map((row) => row.id),
  }
}
