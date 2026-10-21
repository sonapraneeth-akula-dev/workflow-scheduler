import { afterEach, beforeEach, expect, it, vi } from "vitest"
import { changeSystemNotice, getSystemNotice, noticeSchema } from "./api"
import { accessToken } from "@/lib/auth/session"

vi.mock("@/lib/auth/session", () => ({ accessToken: vi.fn() }))
const state = {
  revision: "12",
  notice: { message: "Notice", severity: "Info" as const, expiresAt: null },
  canManage: true,
}
const fetcher = vi.fn<typeof fetch>()
beforeEach(() => {
  vi.resetAllMocks()
  vi.stubGlobal("fetch", fetcher)
  vi.mocked(accessToken).mockResolvedValue("test-access-token")
  fetcher.mockImplementation(
    async () =>
      new Response(JSON.stringify(state), { headers: { ETag: '"12"' } })
  )
})
afterEach(() => vi.unstubAllGlobals())

it("uses authenticated same-origin no-store reads and validates ETag", async () => {
  expect(await getSystemNotice()).toEqual(state)
  expect(fetcher).toHaveBeenCalledWith(
    "/api/v1/system-notice",
    expect.objectContaining({ cache: "no-store" })
  )
  const headers = new Headers(fetcher.mock.calls[0]?.[1]?.headers)
  expect(headers.get("Authorization")).toBe("Bearer test-access-token")
  fetcher.mockResolvedValue(
    new Response(JSON.stringify(state), { headers: { ETag: '"11"' } })
  )
  await expect(getSystemNotice()).rejects.toThrow(
    "inconsistent system notice ETag"
  )
})
it("uses exact preconditions and sends no body on clear", async () => {
  await changeSystemNotice("11", state.notice)
  expect(fetcher).toHaveBeenLastCalledWith(
    "/api/v1/admin/system-notice",
    expect.objectContaining({
      method: "PUT",
      body: JSON.stringify(state.notice),
    })
  )
  expect(new Headers(fetcher.mock.calls[0]?.[1]?.headers).get("If-Match")).toBe(
    '"11"'
  )
  await changeSystemNotice("12", null)
  expect(fetcher).toHaveBeenLastCalledWith(
    "/api/v1/admin/system-notice",
    expect.objectContaining({ method: "DELETE", body: undefined })
  )
})
it("reports notice-specific conflicts, not the schedule conflict text", async () => {
  fetcher.mockResolvedValue(
    new Response('{"title":"Changed"}', { status: 412 })
  )
  await expect(changeSystemNotice("11", null)).rejects.toThrow(
    "This system notice changed elsewhere. Your draft is preserved."
  )
})
it("validates message severity controls revision and expiry before issuing a mutation", () => {
  for (const message of [" ", "\0", "x".repeat(2001)])
    expect(noticeSchema.safeParse({ ...state.notice, message }).success).toBe(
      false
    )
  expect(() => changeSystemNotice("00", state.notice)).toThrow()
  expect(() =>
    changeSystemNotice("11", {
      ...state.notice,
      severity: "Info",
      expiresAt: "2000-01-01T00:00:00Z",
    })
  ).toThrow("Expiry must be in the future")
  expect(fetcher).not.toHaveBeenCalled()
})
