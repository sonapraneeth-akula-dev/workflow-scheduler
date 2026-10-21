import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import type { components } from "@/lib/api/schema"

const revisionSchema = z
  .string()
  .regex(/^(0|[1-9][0-9]{0,18})$/)
  .refine((value) => BigInt(value) <= 9223372036854775807n)
const targetSchema = z.object({
  runId: z.uuid().refine((id) => id === id.toLowerCase() && id[14] === "7"),
  reportId: z
    .uuid()
    .refine((id) => id === id.toLowerCase() && id[14] === "7")
    .optional(),
})
export const signOffReasonSchema = z
  .string()
  .min(1)
  .max(2000)
  .refine((reason) => reason.trim().length > 0 && !reason.includes("\0"))
const eventSchema = z.object({
  revision: revisionSchema.refine((value) => value !== "0"),
  action: z.enum(["SignOff", "Revoke"]),
  reason: signOffReasonSchema,
  actorId: z
    .uuid()
    .refine((id) => id !== "00000000-0000-0000-0000-000000000000"),
  actorName: z.string().min(1).max(512),
  at: z.iso.datetime({ offset: true }),
})
export const signOffStateSchema = z
  .object({
    revision: revisionSchema,
    status: z.enum(["NotSignedOff", "SignedOff", "Revoked"]),
    latest: eventSchema.nullable(),
  })
  .refine(
    (state) =>
      state.revision === "0"
        ? state.status === "NotSignedOff" && state.latest === null
        : state.latest?.revision === state.revision &&
          state.status ===
            (state.latest.action === "SignOff" ? "SignedOff" : "Revoked"),
    "Sign-off state does not match its recorded revision."
  )
const responseSchema = targetSchema.extend({
  reportId: z.uuid().nullable(),
  workflowId: z.string().regex(/^[^/]+\/[^/]+\/[^/]+$/),
  versionId: z.uuid(),
  isTerminal: z.boolean(),
  state: signOffStateSchema,
  history: z.array(eventSchema).max(20),
  nextBefore: revisionSchema.refine((value) => value !== "0").nullable(),
})
export type SignOffTarget = z.infer<typeof targetSchema>
export type SignOffState = z.infer<typeof signOffStateSchema>
export type SignOffEvent = z.infer<typeof eventSchema>
export type SignOffResponse = z.infer<typeof responseSchema>

function readResponse(
  response: Response,
  body: unknown,
  target: SignOffTarget,
  before?: string
) {
  const value = responseSchema.parse(body)
  const revision = BigInt(value.state.revision)
  const first = before
    ? revision < BigInt(before)
      ? revision
      : BigInt(before) - 1n
    : revision
  if (
    response.status !== 200 ||
    response.headers.get("ETag") !== `"${value.state.revision}"` ||
    value.runId !== target.runId ||
    value.reportId !== (target.reportId ?? null) ||
    value.history.length !== Number(first > 20n ? 20n : first) ||
    value.history.some(
      (event, index) => BigInt(event.revision) !== first - BigInt(index)
    ) ||
    value.nextBefore !==
      (first > 20n ? value.history.at(-1)?.revision : null) ||
    (first === revision &&
      first > 0n &&
      JSON.stringify(value.state.latest) !== JSON.stringify(value.history[0]))
  )
    throw new Error("The API returned inconsistent sign-off state or history.")
  return value satisfies components["schemas"]["SignOffResponse"]
}

function failure(response: Response, body: unknown) {
  const error = new ApiError(response, body)
  if (error.status === 412)
    error.message =
      "This sign-off changed elsewhere. Reload sign-off before submitting another change."
  return error
}

export async function getSignOff(
  target: SignOffTarget,
  before?: string,
  signal?: AbortSignal
) {
  const { runId: id, reportId } = targetSchema.parse(target)
  if (before !== undefined)
    revisionSchema.refine((value) => value !== "0").parse(before)
  const query = { before, limit: "20" }
  const result = reportId
    ? await publicApi.GET("/api/v1/runs/{id}/reports/{reportId}/signoff", {
        params: { path: { id, reportId }, query },
        signal,
      })
    : await publicApi.GET("/api/v1/runs/{id}/signoff", {
        params: { path: { id }, query },
        signal,
      })
  if (!result.response.ok) throw failure(result.response, result.error)
  return readResponse(result.response, result.data, target, before)
}

export async function changeSignOff(
  target: SignOffTarget,
  revision: string,
  action: "SignOff" | "Revoke",
  reason: string
) {
  const { runId: id, reportId } = targetSchema.parse(target)
  const header = { "If-Match": `"${revisionSchema.parse(revision)}"` }
  const body = { reason: signOffReasonSchema.parse(reason) }
  const result = reportId
    ? action === "Revoke"
      ? await publicApi.POST(
          "/api/v1/runs/{id}/reports/{reportId}/signoff/revoke",
          { params: { path: { id, reportId }, header }, body }
        )
      : await publicApi.POST("/api/v1/runs/{id}/reports/{reportId}/signoff", {
          params: { path: { id, reportId }, header },
          body,
        })
    : action === "Revoke"
      ? await publicApi.POST("/api/v1/runs/{id}/signoff/revoke", {
          params: { path: { id }, header },
          body,
        })
      : await publicApi.POST("/api/v1/runs/{id}/signoff", {
          params: { path: { id }, header },
          body,
        })
  if (!result.response.ok) throw failure(result.response, result.error)
  const value = readResponse(result.response, result.data, target)
  if (
    BigInt(value.state.revision) !== BigInt(revision) + 1n ||
    value.state.latest?.action !== action ||
    value.state.latest?.reason !== reason.trim()
  )
    throw new Error("The API did not confirm the requested sign-off change.")
  return value
}
