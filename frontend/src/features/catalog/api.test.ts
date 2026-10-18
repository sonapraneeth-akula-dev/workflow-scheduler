import { beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import { accessDenied, getTeams, getTypes, getWorkflows } from "./api"

const { get } = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { GET: get },
}))

const version = { id: "019ed132-2f44-7000-8000-000000000001", semVer: "1.0.0" }
const workflow = {
  id: "team/release/build",
  team: "team",
  type: "release",
  name: "build",
  activeVersion: null,
  latestVersion: version,
  metadata: { type: "release", values: {} },
  latestRun: null,
}

function respond(data: unknown) {
  get.mockResolvedValue({ data, response: new Response() })
}

describe("catalogue API trust boundaries", () => {
  beforeEach(() => vi.resetAllMocks())

  it("uses the authorized team list without exposing registry presentation", async () => {
    respond([{ team: "team", types: [{ team: "team", name: "release" }] }])
    expect(await getTeams()).toEqual([
      { team: "team", typeCount: 1, presentation: null },
    ])
  })

  it("rejects types leaked through another team's registry", async () => {
    respond([{ team: "team", types: [{ team: "foreign", name: "release" }] }])
    await expect(getTeams()).rejects.toThrow("outside its team")
  })

  it("preserves only the owning team's complete presentation contract", async () => {
    const presentation = {
      typeOrder: ["release"],
      groupingKeys: ["environment"],
      listColumns: ["environment"],
      defaultFilters: { environment: "dev" },
      labels: { environment: "Environment" },
    }
    respond([
      {
        team: "team",
        types: [{ team: "team", name: "release" }],
        presentation,
      },
    ])
    expect((await getTeams())[0]?.presentation).toEqual(presentation)
    respond([
      {
        team: "team",
        types: [],
        presentation: { labels: { environment: "Environment" } },
      },
    ])
    await expect(getTeams()).rejects.toThrow()
  })

  it("emits actual dotted metadata keys for both workflow pages and type counts", async () => {
    respond({ items: [], nextCursor: null })
    await getWorkflows("team", "release", null, undefined, {
      environment: "dev",
      optional: "",
    })
    expect(get).toHaveBeenCalledWith("/api/v1/workflows", {
      params: {
        query: {
          team: "team",
          type: "release",
          limit: 50,
          "meta.environment": "dev",
          "meta.optional": "",
        },
      },
      signal: undefined,
    })
    respond([])
    await getTypes("team", undefined, { environment: "dev" })
    expect(get).toHaveBeenLastCalledWith("/api/v1/teams/{team}/types", {
      params: { path: { team: "team" }, query: { "meta.environment": "dev" } },
      signal: undefined,
    })
  })

  it("opts into server-side presentation defaults without transporting stored values", async () => {
    respond({ items: [], nextCursor: null })
    await getWorkflows("team", "release", null, undefined, {}, true)
    expect(get).toHaveBeenLastCalledWith("/api/v1/workflows", {
      params: {
        query: { team: "team", type: "release", limit: 50, defaults: true },
      },
      signal: undefined,
    })
    respond([])
    await getTypes("team", undefined, { environment: "prod" }, true)
    expect(get).toHaveBeenLastCalledWith("/api/v1/teams/{team}/types", {
      params: {
        path: { team: "team" },
        query: { defaults: true, "meta.environment": "prod" },
      },
      signal: undefined,
    })
  })

  it("parses partial string counts without inventing absent statuses", async () => {
    respond([
      {
        team: "team",
        name: "release",
        description: "Deploy",
        latestStatusCounts: { Running: "2", NeverRun: 1 },
        metadataSchema: { keys: [], allowFreeForm: true },
      },
    ])
    expect((await getTypes("team"))[0]?.latestStatusCounts).toEqual({
      Running: 2,
      NeverRun: 1,
    })
  })

  it.each([-1, "01", "9007199254740992", 1.5, null, "invalid"])(
    "rejects invalid status count %j",
    async (value) => {
      respond([
        {
          team: "team",
          name: "release",
          description: "",
          latestStatusCounts: { Running: value },
          metadataSchema: { keys: [], allowFreeForm: true },
        },
      ])
      await expect(getTypes("team")).rejects.toThrow()
    }
  )

  it("rejects another team's type response", async () => {
    respond([
      {
        team: "foreign",
        name: "release",
        description: "",
        latestStatusCounts: {},
        metadataSchema: { keys: [], allowFreeForm: true },
      },
    ])
    await expect(getTypes("team")).rejects.toThrow("different team's")
  })

  it("forwards the exact type name, bounded page, cursor and cancellation", async () => {
    respond({ items: [workflow], nextCursor: "next" })
    const signal = new AbortController().signal
    expect(await getWorkflows("team", "release", "cursor", signal)).toEqual({
      items: [workflow],
      nextCursor: "next",
    })
    expect(get).toHaveBeenCalledWith("/api/v1/workflows", {
      params: {
        query: { team: "team", type: "release", limit: 50, cursor: "cursor" },
      },
      signal,
    })
  })

  it.each([
    { ...workflow, team: "foreign" },
    { ...workflow, type: "foreign" },
    { ...workflow, id: "team/release/other" },
    { ...workflow, latestRun: { status: "unknown" } },
  ])("rejects untrusted workflow identity or state", async (item) => {
    respond({ items: [item], nextCursor: null })
    await expect(getWorkflows("team", "release", null)).rejects.toThrow()
  })

  it("rejects an unbounded server page", async () => {
    respond({
      items: Array.from({ length: 51 }, () => workflow),
      nextCursor: null,
    })
    await expect(getWorkflows("team", "release", null)).rejects.toThrow()
  })

  it.each([401, 403, 404, 502])(
    "surfaces HTTP %i without an empty success fallback",
    async (status) => {
      get.mockResolvedValue({
        response: new Response(null, { status }),
        error: { title: "Catalogue unavailable" },
      })
      await expect(getTeams()).rejects.toMatchObject({ status })
      expect(
        accessDenied(new ApiError(new Response(null, { status }), {}))
      ).toBe(status !== 502)
    }
  )
})
