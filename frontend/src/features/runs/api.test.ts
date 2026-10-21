import { beforeEach, describe, expect, it, vi } from "vitest"
import { getRuns } from "./api"

const { get } = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { GET: get },
}))
const row = {
  signOff: { revision: "0", status: "NotSignedOff", latest: null },
  id: "019ed132-2f44-7000-8000-000000000010",
  workflowId: "team/release/build",
  versionId: "019ed132-2f44-7000-8000-000000000001",
  seriesId: null,
  team: "team",
  type: "release",
  status: "Running",
  createdAt: "2026-10-16T07:00:00.000001Z",
  startedAt: null,
  endedAt: null,
  metadata: { type: "release", values: { environment: " old & = prod " } },
}
function respond(items: unknown[], nextCursor: string | null = null) {
  get.mockResolvedValue({
    response: new Response(),
    data: { items, nextCursor },
  })
}

describe("run search response boundaries", () => {
  beforeEach(() => vi.resetAllMocks())

  it("emits exact dotted metadata query keys and does not send UI grouping", async () => {
    respond([row], "next")
    const signal = new AbortController().signal
    expect(
      (
        await getRuns(
          {
            team: "team",
            type: "release",
            status: "Running",
            metadata: { environment: " old & = prod " },
            from: "2026-10-16T07:00:00.000001Z",
            to: "2026-10-16T07:00:00.000002Z",
          },
          null,
          signal
        )
      ).items
    ).toEqual([row])
    expect(get).toHaveBeenCalledWith("/api/v1/runs", {
      params: {
        query: {
          team: "team",
          type: "release",
          status: "Running",
          workflowId: undefined,
          seriesId: undefined,
          cursor: undefined,
          from: "2026-10-16T07:00:00.000001Z",
          to: "2026-10-16T07:00:00.000002Z",
          limit: 50,
          "meta.environment": " old & = prod ",
        },
      },
      signal,
    })
  })

  it.each([
    [{ ...row, team: "foreign" }],
    [{ ...row, workflowId: "team/foreign/build" }],
    [{ ...row, status: "Succeeded" }],
    [
      {
        ...row,
        metadata: {
          type: "release",
          values: { environment: "current-not-historical" },
        },
      },
    ],
    [{ ...row, createdAt: "2026-10-16T07:00:00.000000Z" }],
    [{ ...row, createdAt: "2026-10-16T07:00:00.000002Z" }],
    [row, row],
    [{ ...row, id: "019ed132-2f44-7000-8000-000000000009" }, row],
  ])(
    "rejects leaked, unordered, duplicate or nonmatching runs",
    async (...items) => {
      respond(items)
      await expect(
        getRuns(
          {
            team: "team",
            type: "release",
            status: "Running",
            metadata: { environment: " old & = prod " },
            from: "2026-10-16T07:00:00.000001Z",
            to: "2026-10-16T07:00:00.000002Z",
          },
          null
        )
      ).rejects.toThrow("inconsistent")
    }
  )

  it("requires cursor progress and cross-page descending identity", async () => {
    const cursor = { cursor: "old", afterId: row.id }
    respond([row], "next")
    await expect(getRuns({}, cursor)).rejects.toThrow("inconsistent")
    respond([{ ...row, id: "019ed132-2f44-7000-8000-000000000009" }], "old")
    await expect(getRuns({}, cursor)).rejects.toThrow("inconsistent")
    respond([], "next")
    await expect(getRuns({}, null)).rejects.toThrow("inconsistent")
  })

  it("does not infer missing historical metadata or accept it as an exact match", async () => {
    respond([{ ...row, metadata: null }])
    expect((await getRuns({}, null)).items[0]?.metadata).toBeNull()
    await expect(
      getRuns({ metadata: { environment: "prod" } }, null)
    ).rejects.toThrow("inconsistent")
  })

  it("rejects oversized and malformed pages and surfaces actual API errors", async () => {
    respond(Array.from({ length: 51 }, () => row))
    await expect(getRuns({}, null)).rejects.toThrow()
    respond([{ ...row, status: 1 }])
    await expect(getRuns({}, null)).rejects.toThrow()
    get.mockResolvedValue({
      response: new Response(null, { status: 403 }),
      error: { title: "No Viewer grant." },
    })
    await expect(getRuns({}, null)).rejects.toThrow("No Viewer grant.")
  })
})
