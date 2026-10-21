import { z } from "zod"
import { ApiError } from "@/lib/api/client"
import { accessToken } from "@/lib/auth/session"
import { parseJson } from "@/lib/json"
import type { components } from "@/lib/api/schema"

const text = z
  .string()
  .min(1)
  .max(2048)
  .regex(/^[^\p{Cc}]+$/u)
const count = z.int().nonnegative()
const timestamp = z.iso.datetime({ offset: true })
const identity = z
  .string()
  .regex(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/)
  .refine((value) => value !== "00000000-0000-0000-0000-000000000000")
const condition = z.object({
  type: text,
  status: text,
  reason: text.nullable(),
})
export const deploymentSnapshotSchema = z
  .object({
    observedAt: timestamp,
    staleAfterSeconds: z.int().positive(),
    namespace: text,
    workloads: z
      .array(
        z.object({
          name: text,
          kind: z.enum(["Deployment", "StatefulSet", "DaemonSet"]),
          strategy: text,
          generation: count,
          observedGeneration: count.nullable(),
          desired: count,
          updated: count,
          ready: count,
          available: count,
          matchingImages: count,
          state: z.enum([
            "Unknown",
            "Blocked",
            "InSync",
            "AwaitingReplacement",
            "Updating",
          ]),
          targetImages: z.record(text, text),
          conditions: z.array(condition),
        })
      )
      .length(4),
    pods: z
      .array(
        z.object({
          name: text,
          uid: text,
          role: text,
          nodeName: text,
          phase: text,
          ready: z.boolean(),
          terminating: z.boolean(),
          createdAt: timestamp,
          conditions: z.array(condition),
          containers: z.array(
            z.object({
              name: text,
              init: z.boolean(),
              image: text,
              imageId: text.nullable(),
              state: z.enum(["Waiting", "Running", "Terminated", "Unknown"]),
              reason: text.nullable(),
              exitCode: z.int().nullable(),
              restarts: count,
              ready: z.boolean(),
            })
          ),
        })
      )
      .max(500),
    jobs: z
      .array(
        z.object({
          name: text,
          createdAt: timestamp,
          finishedAt: timestamp.nullable(),
          state: z.enum(["Failed", "Succeeded", "Running", "Pending"]),
          active: count,
          succeeded: count,
          failed: count,
          images: z.array(text),
          conditions: z.array(condition),
        })
      )
      .max(20),
  })
  .refine(
    (value) =>
      new Set(value.workloads.map((row) => row.name)).size ===
        value.workloads.length &&
      new Set(value.pods.map((row) => row.uid)).size === value.pods.length &&
      new Set(value.jobs.map((row) => row.name)).size === value.jobs.length,
    "Deployment observations contain duplicate identities."
  ) satisfies z.ZodType<components["schemas"]["DeploymentSnapshot"]>
const phase = z.enum([
  "Preflight",
  "Build",
  "Import",
  "Configure",
  "Migrate",
  "Rollout",
  "Controller",
  "Drain",
  "Replace",
  "Resume",
  "Verify",
  "Complete",
  "Failed",
])
export const deploymentHistorySchema = z.object({
  observedAt: timestamp,
  unconfirmedAfterSeconds: z.int().positive(),
  operations: z
    .array(
      z
        .object({
          id: identity,
          mode: z.enum(["Stage", "Release"]),
          target: text,
          actor: identity,
          actorKind: z.enum(["User", "App"]),
          startedAt: timestamp,
          lastReportedAt: timestamp,
          status: z.enum(["Running", "Succeeded", "Failed"]),
          sequence: count.max(128),
          events: z
            .array(
              z.object({
                sequence: z.int().min(1).max(128),
                at: timestamp,
                phase,
                subject: text.nullable(),
                outcome: z.enum(["Running", "Succeeded", "Failed"]),
                description: text,
              })
            )
            .max(128),
        })
        .refine(
          (row) =>
            row.events.length === row.sequence &&
            row.events.every((event, index) => event.sequence === index + 1) &&
            (row.status === "Running"
              ? row.events.every(
                  (event) =>
                    event.phase !== "Complete" && event.phase !== "Failed"
                )
              : row.events.at(-1)?.phase ===
                (row.status === "Succeeded" ? "Complete" : "Failed")),
          "Deployment operation history is inconsistent."
        )
    )
    .max(20),
}) satisfies z.ZodType<components["schemas"]["DeploymentActivityHistory"]>
export type DeploymentSnapshot = z.infer<typeof deploymentSnapshotSchema>
export type DeploymentHistory = z.infer<typeof deploymentHistorySchema>

async function read<T>(
  path: string,
  schema: z.ZodType<T>,
  signal?: AbortSignal
): Promise<T> {
  const token = await accessToken()
  const headers = new Headers()
  if (token) headers.set("Authorization", `Bearer ${token}`)
  const response = await fetch(path, { headers, signal, cache: "no-store" })
  const body = await response.text()
  if (!response.ok)
    throw new ApiError(
      response,
      body.startsWith("{") ? parseJson(body) : undefined
    )
  return schema.parse(parseJson(body))
}

export const getDeploymentSnapshot = (signal?: AbortSignal) =>
  read("/api/v1/admin/system/deployments", deploymentSnapshotSchema, signal)
export const getDeploymentHistory = (signal?: AbortSignal) =>
  read("/api/v1/admin/deployment-activity", deploymentHistorySchema, signal)
