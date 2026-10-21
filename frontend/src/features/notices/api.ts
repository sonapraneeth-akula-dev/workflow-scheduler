import { z } from "zod"
import { ApiError } from "@/lib/api/client"
import { accessToken } from "@/lib/auth/session"
import { parseJson } from "@/lib/json"

export const noticeSchema = z.object({
  message: z
    .string()
    .min(1)
    .max(2000)
    .refine(
      (value) =>
        value.trim().length > 0 &&
        !/[\p{Cc}]/u.test(value.replace(/[\r\n\t]/g, "")),
      "Use nonblank plain text without control characters."
    ),
  severity: z.enum(["Info", "Warning", "Critical"]),
  expiresAt: z.iso.datetime({ offset: true }).nullable(),
})
const revisionSchema = z
  .string()
  .regex(/^(0|[1-9][0-9]{0,18})$/)
  .refine((value) => BigInt(value) <= 9223372036854775807n)
const responseSchema = z.object({
  revision: revisionSchema,
  notice: noticeSchema.nullable(),
  canManage: z.boolean(),
})
export type SystemNotice = z.infer<typeof noticeSchema>
export type SystemNoticeSnapshot = z.infer<typeof responseSchema>
export const noticeQueryKey = ["system-notice"] as const

async function request(
  path: string,
  options: RequestInit
): Promise<SystemNoticeSnapshot> {
  const token = await accessToken()
  const headers = new Headers(options.headers)
  if (token) headers.set("Authorization", `Bearer ${token}`)
  const response = await fetch(path, { ...options, headers, cache: "no-store" })
  const text = await response.text()
  if (!response.ok) {
    const error = new ApiError(
      response,
      text.startsWith("{") ? parseJson(text) : undefined
    )
    if (error.status === 412)
      error.message =
        "This system notice changed elsewhere. Your draft is preserved. Reload the latest notice before publishing or clearing."
    throw error
  }
  const value = responseSchema.parse(parseJson(text))
  if (
    response.status !== 200 ||
    response.headers.get("ETag") !== `"${value.revision}"`
  )
    throw new Error(
      "The API returned an inconsistent system notice ETag. Reload before editing."
    )
  return value
}

export function getSystemNotice(signal?: AbortSignal) {
  return request("/api/v1/system-notice", { signal })
}

export function changeSystemNotice(
  revision: string,
  notice: SystemNotice | null
) {
  const headers: HeadersInit = {
    "If-Match": `"${revisionSchema.parse(revision)}"`,
    ...(notice ? { "Content-Type": "application/json" } : {}),
  }
  const body = notice === null ? undefined : noticeSchema.parse(notice)
  if (body?.expiresAt && Date.parse(body.expiresAt) <= Date.now())
    throw new Error("Expiry must be in the future, or blank for no expiry.")
  return request("/api/v1/admin/system-notice", {
    method: notice === null ? "DELETE" : "PUT",
    headers,
    body: body ? JSON.stringify(body) : undefined,
  })
}
