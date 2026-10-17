import { beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import { auditSearchSchema, getAudit, type AuditRow } from "./api"

const { get } = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { GET: get },
}))
const row: AuditRow = {
  id: "9223372036854775807",
  at: "2026-10-17T00:00:00.000001Z",
  actor: "operator",
  actorKind: "User",
  action: "machine.update",
  targetKind: "Machine",
  targetId: "machine",
  teamId: null,
  summary: { before: { pool: "old" }, after: { pool: "new" } },
}
const cursor = "a".repeat(150)
function respond(items: unknown[] = [row], nextCursor: string | null = null) {
  get.mockResolvedValue({
    response: new Response(),
    data: JSON.stringify({ items, nextCursor }),
  })
}
describe("audit API boundary", () => {
  beforeEach(() => vi.resetAllMocks())
  it("uses generated public contract, exact filters, abort signal and text JSON for number precision", async () => {
    respond()
    const signal = new AbortController().signal
    const search = {
      actor: row.actor,
      targetKind: row.targetKind,
      targetId: row.targetId,
      from: "2026-10-17T00:00:00Z",
      to: "2026-10-18T00:00:00Z",
    }
    expect((await getAudit(search, null, signal)).items).toEqual([row])
    expect(get).toHaveBeenCalledWith("/api/v1/audit", {
      params: { query: { ...search, limit: 50, cursor: undefined } },
      signal,
      parseAs: "text",
    })
  })
  it("compares Int64 IDs and microsecond timestamp ties without numeric rounding", async () => {
    respond([row, { ...row, id: "9223372036854775806" }])
    expect((await getAudit({}, null)).items).toHaveLength(2)
    respond([{ ...row, id: "9223372036854775806" }])
    expect((await getAudit({}, { cursor, after: row })).items[0]?.id).toBe(
      "9223372036854775806"
    )
    respond([row])
    await expect(getAudit({}, { cursor, after: row })).rejects.toThrow(
      "inconsistent audit"
    )
  })
  it.each([
    { actor: "" },
    { actor: " " },
    { targetId: "\n" },
    { targetKind: "a".repeat(1025) },
    { from: "2026-10-17" },
    { from: "2026-10-17T00:00:00.0000001Z" },
    { from: "2026-10-17T00:00:00Z", to: "2026-10-17T00:00:00Z" },
  ])(
    "rejects invalid request filters before transport: %j",
    async (filters) => {
      expect(auditSearchSchema.safeParse(filters).success).toBe(false)
      await expect(getAudit(filters, null)).rejects.toThrow()
      expect(get).not.toHaveBeenCalled()
    }
  )
  it.each([
    { id: 123 },
    { id: "0" },
    { id: "01" },
    { id: "9223372036854775808" },
    { actorKind: "Unknown" },
    { summary: [] },
    { summary: null },
    { teamId: "" },
    { at: "2026-10-17" },
  ])("rejects malformed committed audit data: %j", async (change) => {
    respond([{ ...row, ...change }])
    await expect(getAudit({}, null)).rejects.toThrow()
  })
  it.each([
    { actor: "other" },
    { targetKind: "Run" },
    { targetId: "other" },
    { from: "2026-10-17T00:00:00.000002Z" },
    { to: row.at },
  ])(
    "rejects a response violating the requested filter: %j",
    async (filter) => {
      respond()
      await expect(getAudit(filter, null)).rejects.toThrow("inconsistent audit")
    }
  )
  it("rejects duplicate, ascending, oversized and nonprogressing pages", async () => {
    for (const items of [
      [row, row],
      [{ ...row, id: "1" }, row],
      [row, { ...row, at: "2026-10-17T00:00:00.000002Z" }],
    ]) {
      respond(items)
      await expect(getAudit({}, null)).rejects.toThrow("inconsistent audit")
    }
    respond(Array.from({ length: 51 }, () => row))
    await expect(getAudit({}, null)).rejects.toThrow()
    respond([], cursor)
    await expect(getAudit({}, null)).rejects.toThrow("inconsistent audit")
    respond([row], cursor)
    await expect(
      getAudit({}, { cursor, after: { ...row, at: "2026-10-18T00:00:00Z" } })
    ).rejects.toThrow("inconsistent audit")
  })
  it("passes original cursor and accepts only strictly older next page", async () => {
    respond([{ ...row, at: "2026-10-16T23:59:59.999999Z" }])
    await getAudit({}, { cursor, after: row })
    expect(get.mock.calls[0]?.[1].params.query.cursor).toBe(cursor)
  })
  it("reports HTTP failures without accepting success-shaped error data", async () => {
    get.mockResolvedValue({
      response: new Response(null, { status: 403 }),
      data: JSON.stringify({ items: [row], nextCursor: null }),
    })
    await expect(getAudit({}, null)).rejects.toBeInstanceOf(ApiError)
    get.mockResolvedValue({ response: new Response(), data: undefined })
    await expect(getAudit({}, null)).rejects.toThrow("empty audit")
  })
})
