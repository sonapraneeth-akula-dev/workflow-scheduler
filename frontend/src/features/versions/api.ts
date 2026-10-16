import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import { jsonValueSchema, parseJson } from "@/lib/json"

const key = z
  .string()
  .min(1)
  .regex(/^[^/\\\p{Cc}]+$/u)
  .refine((value) => value !== "." && value !== "..")
const versionId = z
  .uuid()
  .refine(
    (value) =>
      value === value.toLowerCase() &&
      value !== "00000000-0000-0000-0000-000000000000"
  )
const workflowId = z
  .string()
  .regex(/^[^/\\\p{Cc}]+\/[^/\\\p{Cc}]+\/[^/\\\p{Cc}]+$/u)
export const registrationSchema = z.object({
  gitCommit: z.string().regex(/^[0-9a-f]{40}([0-9a-f]{24})?$/),
  gitAuthor: z.string().min(1),
  registeredBy: z.string().min(1),
  changeNote: z.string().nullable(),
  sdkVersion: z.string().min(1),
  registeredAt: z.iso.datetime({ offset: true }),
})
export type VersionRegistration = z.infer<typeof registrationSchema>

const summary = z.object({
  versionId,
  workflowId,
  version: z.string().min(1),
  registration: registrationSchema,
})
export const versionsPageSchema = z
  .object({
    items: z.array(summary).max(50),
    nextCursor: z.string().min(1).max(256).nullable(),
    activeVersionId: versionId.nullable(),
    latestVersionId: versionId.nullable(),
  })
  .superRefine((page, context) => {
    if (
      new Set(page.items.map((item) => item.versionId)).size !==
        page.items.length ||
      (page.nextCursor !== null && page.items.length !== 50)
    )
      context.addIssue({
        code: "custom",
        message:
          "The version history page contains inconsistent identities or pagination.",
      })
  })

const change = z
  .object({
    key: z.string().min(1),
    kind: z.enum(["Added", "Removed", "Changed"]),
    before: jsonValueSchema,
    after: jsonValueSchema,
  })
  .superRefine((entry, context) => {
    if (
      entry.kind === "Added"
        ? entry.before !== null || entry.after === null
        : entry.kind === "Removed"
          ? entry.before === null || entry.after !== null
          : entry.before === null || entry.after === null
    )
      context.addIssue({
        code: "custom",
        message: "Manifest change snapshots do not match their change kind.",
      })
  })
const changes = z.array(change).superRefine((entries, context) => {
  if (new Set(entries.map((entry) => entry.key)).size !== entries.length)
    context.addIssue({
      code: "custom",
      message: "Manifest change keys must be unique.",
    })
})
const sourceText = z
  .string()
  .max(1024 * 1024)
  .refine(
    (text) => new TextEncoder().encode(text).length <= 1024 * 1024,
    "Workflow source exceeds its UTF-8 byte limit."
  )
export const versionDiffSchema = z
  .object({
    workflowId,
    fromVersionId: versionId,
    toVersionId: versionId,
    fromVersion: z.string().min(1),
    toVersion: z.string().min(1),
    manifest: z.object({
      nodes: changes,
      variables: changes,
      policies: changes,
      settings: changes,
    }),
    source: z.object({
      path: z.literal("workflow.cs"),
      before: sourceText,
      after: sourceText,
      changed: z.boolean(),
    }),
  })
  .superRefine((diff, context) => {
    if (diff.source.changed !== (diff.source.before !== diff.source.after))
      context.addIssue({
        code: "custom",
        message: "Source diff change state is inconsistent.",
      })
  })
export type VersionDiff = z.infer<typeof versionDiffSchema>

const cloneKey = key.refine(
  (value) => value.trim().length > 0 && [...value].length <= 128
)
export const cloneRequestSchema = z
  .object({
    newName: cloneKey,
    targetType: z
      .string()
      .refine(
        (value) =>
          z.tuple([cloneKey, cloneKey]).safeParse(value.split("/")).success
      ),
  })
  .strict()
export type CloneRequest = z.infer<typeof cloneRequestSchema>

export async function cloneWorkflow(
  identity: string,
  version: string,
  request: CloneRequest
): Promise<Blob> {
  versionId.parse(version)
  const body = cloneRequestSchema.parse(request)
  if (`${body.targetType}/${body.newName}` === identity) {
    throw new Error("The clone target must differ from the source workflow.")
  }
  const result = await publicApi.POST(
    "/api/v1/workflows/{team}/{type}/{workflow}/versions/{version}/clone",
    {
      params: { path: { ...path(identity), version } },
      body,
      parseAs: "stream",
      redirect: "error",
    }
  )
  if (!result.response.ok) {
    throw new ApiError(result.response, result.error)
  }
  const limit = 2 * 1024 * 1024
  const length = result.response.headers.get("Content-Length")
  if (
    result.response.headers.get("Content-Type")?.split(";")[0] !==
      "application/zip" ||
    !result.response.headers
      .get("Content-Disposition")
      ?.startsWith("attachment;") ||
    !result.response.headers
      .get("Cache-Control")
      ?.split(",")
      .some((value) => value.trim() === "no-store") ||
    (length !== null && (!/^[0-9]+$/.test(length) || Number(length) > limit)) ||
    !result.data
  ) {
    await result.data?.cancel()
    throw new Error("The API did not return a bounded no-store ZIP attachment.")
  }
  const reader = result.data.getReader()
  const chunks: Uint8Array<ArrayBuffer>[] = []
  let size = 0
  try {
    while (true) {
      const { done, value } = await reader.read()
      if (done) {
        break
      }
      size += value.byteLength
      if (size > limit) {
        throw new Error("Clone download exceeds its size limit.")
      }
      chunks.push(new Uint8Array(value))
    }
    if (size === 0 || (length !== null && size !== Number(length))) {
      throw new Error("Clone download was empty or incomplete.")
    }
    return new Blob(chunks, { type: "application/zip" })
  } finally {
    await reader.cancel().catch(() => undefined)
    reader.releaseLock()
  }
}

function path(identity: string) {
  const [team, type, workflow] = z
    .tuple([key, key, key])
    .parse(identity.split("/"))
  return { team, type, workflow }
}

export async function getVersions(
  identity: string,
  cursor: string | null,
  signal?: AbortSignal
) {
  if (cursor !== null) z.string().min(1).max(256).parse(cursor)
  const result = await publicApi.GET(
    "/api/v1/workflows/{team}/{type}/{workflow}/versions",
    {
      params: {
        path: path(identity),
        query: { limit: 50, ...(cursor ? { cursor } : {}) },
      },
      signal,
    }
  )
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  const page = versionsPageSchema.parse(result.data)
  if (
    page.items.some((item) => item.workflowId !== identity) ||
    (cursor !== null && page.nextCursor === cursor)
  )
    throw new Error(
      "The API returned another workflow's history or a repeated version cursor."
    )
  return page
}

export async function getVersionDiff(
  identity: string,
  from: string,
  to: string,
  signal?: AbortSignal
) {
  versionId.parse(from)
  versionId.parse(to)
  const result = await publicApi.GET(
    "/api/v1/workflows/{team}/{type}/{workflow}/diff",
    {
      params: { path: path(identity), query: { from, to } },
      signal,
      parseAs: "text",
    }
  )
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  if (typeof result.data !== "string")
    throw new Error("The API returned an empty version diff.")
  const diff = versionDiffSchema.parse(parseJson(result.data))
  if (
    diff.workflowId !== identity ||
    diff.fromVersionId !== from ||
    diff.toVersionId !== to
  )
    throw new Error("The API returned a different workflow version pair.")
  return diff
}
