import { beforeEach, describe, expect, it, vi } from "vitest"
import { getHistory, historySearchSchema, type HistoryRow } from "./history"

const { get } = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { GET: get },
}))
export const historyRow: HistoryRow = {
  signOff: { revision: "0", status: "NotSignedOff", latest: null },
  kind: "Run",
  at: "2026-10-16T07:00:00.000001Z",
  workflowId: "team/release/build",
  seriesId: "019ed132-2f44-7000-8000-000000000003",
  runId: "019ed132-2f44-7000-8000-000000000010",
  versionId: "019ed132-2f44-7000-8000-000000000001",
  version: "1.2.3-rc.1+build.7",
  status: "Aborted",
  startedAt: "2026-10-16T07:00:00.000002Z",
  endedAt: "2026-10-16T07:00:01.000003Z",
  durationMs: 1000.001,
  trigger: "Manual",
  metadata: { type: "release", values: {} },
  abort: {
    by: "019ed132-2f44-7000-8000-000000000004",
    byName: "Original operator",
    at: "2026-10-16T07:00:00.1234567Z",
    reason: "Stop this deployment",
    source: "UI",
  },
  variables: [{ key: "credential", type: "Secret", redacted: true }],
  fire: null,
}
const scope = { workflowId: historyRow.workflowId }
function respond(
  items: unknown[] = [historyRow],
  nextCursor: string | null = null,
  currentScheduleId: string | null = null
) {
  get.mockResolvedValue({
    response: new Response(),
    data: { items, nextCursor, currentScheduleId },
  })
}

describe("history contracts", () => {
  beforeEach(() => vi.resetAllMocks())
  it("retains recorded triggering names and accepts missing legacy attribution", async () => {
    for (const triggeredByName of [
      "Original triggering operator",
      null,
      undefined,
    ]) {
      respond([{ ...historyRow, triggeredByName }])
      expect(
        (await getHistory(scope, {}, null)).items[0]?.triggeredByName
      ).toBe(triggeredByName)
    }
  })
  it.each(["", " ", 1])(
    "rejects invalid recorded triggering names: %s",
    async (triggeredByName) => {
      respond([{ ...historyRow, triggeredByName }])
      await expect(getHistory(scope, {}, null)).rejects.toThrow()
    }
  )
  it("accepts canonical principal GUIDs without imposing run UUID version/variant bits", () => {
    expect(
      historySearchSchema.parse({
        abortedBy: "11111111-1111-1111-1111-111111111111",
      })
    ).toEqual({
      abortedBy: "11111111-1111-1111-1111-111111111111",
    })
  })
  it("requests exact scope, typed filters and bounded paging", async () => {
    const signal = new AbortController().signal
    respond()
    const search = {
      status: "Aborted",
      abortedBy: historyRow.abort!.by,
      sort: "createdAtAsc",
      outcome: "Run",
    } as const
    expect((await getHistory(scope, search, null, signal)).items).toEqual([
      historyRow,
    ])
    expect(get).toHaveBeenCalledWith("/api/v1/runs/history", {
      params: { query: { ...scope, ...search, limit: 50, cursor: undefined } },
      signal,
    })
  })
  it("passes selected schedule sources and applies the requested card run limit", async () => {
    respond()
    const filters = {
      seriesIds: historyRow.seriesId!,
      includeManual: "false",
      outcome: "Run",
    } as const
    expect(
      (await getHistory(scope, filters, null, undefined, 1)).items
    ).toHaveLength(1)
    expect(get).toHaveBeenLastCalledWith("/api/v1/runs/history", {
      params: { query: { ...scope, ...filters, limit: 1, cursor: undefined } },
      signal: undefined,
    })
    respond([{ ...historyRow, seriesId: null }])
    await expect(getHistory(scope, filters, null)).rejects.toThrow(
      /inconsistent history/
    )
    respond()
    await expect(
      getHistory(scope, { seriesIds: "none" }, null)
    ).rejects.toThrow(/inconsistent history/)
  })
  it("accepts URL-parsed booleans without weakening API filter validation", () => {
    expect(historySearchSchema.parse({ includeManual: false })).toEqual({
      includeManual: "false",
    })
    expect(historySearchSchema.parse({ includeManual: true })).toEqual({
      includeManual: "true",
    })
    expect(historySearchSchema.safeParse({ includeManual: 0 }).success).toBe(
      false
    )
  })
  it.each(["", "not-a-uuid", `${historyRow.seriesId},${historyRow.seriesId}`])(
    "rejects invalid schedule selections before requesting data: %s",
    async (seriesIds) => {
      await expect(getHistory(scope, { seriesIds }, null)).rejects.toThrow()
      expect(get).not.toHaveBeenCalled()
    }
  )
  it("retains authoritative schedule context on an empty filtered series page", async () => {
    respond([], null, historyRow.versionId)
    expect(
      (
        await getHistory(
          { seriesId: historyRow.seriesId! },
          { status: "Succeeded" },
          null
        )
      ).currentScheduleId
    ).toBe(historyRow.versionId)
  })
  it("keeps genuine fire-only facts without inventing status, duration, version or variables", async () => {
    const fire: HistoryRow = {
      ...historyRow,
      kind: "Fire",
      signOff: null,
      runId: null,
      versionId: null,
      version: null,
      status: null,
      startedAt: null,
      endedAt: null,
      durationMs: null,
      trigger: null,
      metadata: null,
      abort: null,
      variables: [],
      fire: {
        scheduleId: historyRow.versionId!,
        fireAt: historyRow.at,
        outcome: "Missed",
        reason: null,
        scheduleConfigVersionId: null,
      },
    }
    respond([fire])
    expect(
      (await getHistory(scope, { outcome: "Missed" }, null)).items
    ).toEqual([fire])
    respond([
      {
        ...fire,
        runId: historyRow.runId,
        fire: { ...fire.fire, reason: "catch-up" },
      },
    ])
    expect((await getHistory(scope, {}, null)).items[0]?.status).toBeNull()
  })
  it.each([
    { workflowId: "foreign/release/build" },
    { kind: "Other" },
    { status: "Unknown" },
    { versionId: null },
    { version: "" },
    { version: 1 },
    { version: undefined },
    { version: "x".repeat(257) },
    { durationMs: -1 },
    { durationMs: 1000 },
    { startedAt: null },
    { variables: [{ key: "credential", type: "Secret", redacted: false }] },
    { variables: [{ key: "x", type: "String", redacted: true }] },
    { variables: [{ key: "x", type: "Unknown", redacted: false }] },
    {
      variables: [
        { key: "x", type: "String", redacted: false },
        { key: "x", type: "String", redacted: false },
      ],
    },
    { kind: "Fire" },
  ])("rejects inconsistent or out-of-scope facts: %j", async (changes) => {
    respond([{ ...historyRow, ...changes }])
    await expect(getHistory(scope, {}, null)).rejects.toThrow()
  })
  it("rejects mismatched exact filters and series identity", async () => {
    respond()
    await expect(
      getHistory(scope, { status: "Succeeded" }, null)
    ).rejects.toThrow("inconsistent")
    await expect(
      getHistory(scope, { abortedBy: historyRow.versionId! }, null)
    ).rejects.toThrow("inconsistent")
    await expect(
      getHistory({ seriesId: historyRow.versionId! }, {}, null)
    ).rejects.toThrow("inconsistent")
    await expect(
      getHistory(scope, { outcome: "Skipped" }, null)
    ).rejects.toThrow("inconsistent")
  })
  it("does not invent semantic versions for retained runs without a version registration", async () => {
    respond([{ ...historyRow, version: null }])
    expect((await getHistory(scope, {}, null)).items[0]?.version).toBeNull()
  })
  it("checks microsecond ordering and page boundaries in both directions", async () => {
    const older = { ...historyRow, at: "2026-10-16T07:00:00.000000Z" }
    respond([historyRow, older], "next")
    await expect(getHistory(scope, {}, null)).resolves.toHaveProperty(
      "nextCursor",
      "next"
    )
    await expect(
      getHistory(scope, { sort: "createdAtAsc" }, null)
    ).rejects.toThrow("inconsistent")
    respond([older, historyRow])
    await expect(
      getHistory(scope, { sort: "createdAtAsc" }, null)
    ).resolves.toBeDefined()
    respond([historyRow])
    await expect(
      getHistory(scope, {}, { cursor: "previous", after: historyRow })
    ).rejects.toThrow("inconsistent")
    respond([older], "previous")
    await expect(
      getHistory(scope, {}, { cursor: "previous", after: historyRow })
    ).rejects.toThrow("inconsistent")
    respond([], "next")
    await expect(getHistory(scope, {}, null)).rejects.toThrow("inconsistent")
  })
  it("strips unprojected values instead of displaying them", async () => {
    respond([
      {
        ...historyRow,
        variables: [
          { ...historyRow.variables[0], value: "private", default: "private" },
        ],
      },
    ])
    expect(JSON.stringify(await getHistory(scope, {}, null))).not.toContain(
      "private"
    )
  })
  it("requires nullable context, bounded pages and surfaces denial", async () => {
    get.mockResolvedValue({
      response: new Response(),
      data: { items: [], nextCursor: null },
    })
    await expect(getHistory(scope, {}, null)).rejects.toThrow()
    respond(Array.from({ length: 51 }, () => historyRow))
    await expect(getHistory(scope, {}, null)).rejects.toThrow()
    get.mockResolvedValue({
      response: new Response(null, { status: 403 }),
      error: { title: "Viewer revoked." },
    })
    await expect(getHistory(scope, {}, null)).rejects.toThrow("Viewer revoked.")
  })
  it.each([
    "invalid",
    "00000000-0000-0000-0000-000000000000",
    "019ED132-2F44-7000-8000-000000000004",
  ])("rejects invalid principal %s before fetch", (abortedBy) => {
    expect(historySearchSchema.safeParse({ abortedBy }).success).toBe(false)
    expect(get).not.toHaveBeenCalled()
  })
})
