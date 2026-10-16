import { beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import { getVersionDiff, getVersions, type VersionDiff } from "./api"

const { get } = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { GET: get },
}))
const workflowId = "team/type/workflow"
const from = "019ed132-2f44-7000-8000-000000000001"
const to = "019ed132-2f44-7000-8000-000000000002"
const registration = {
  gitCommit: "a".repeat(40),
  gitAuthor: "<script>Author</script>",
  registeredBy: "registrar",
  changeNote: null,
  sdkVersion: "1.0.0",
  registeredAt: "2026-10-16T12:00:00.123456+00:00",
}
const page = {
  items: [
    { versionId: from, workflowId, version: "1.0.0-rc.1+build", registration },
  ],
  nextCursor: null,
  activeVersionId: from,
  latestVersionId: to,
}
const diff: VersionDiff = {
  workflowId,
  fromVersionId: from,
  toVersionId: to,
  fromVersion: "1.0.0",
  toVersion: "2.0.0",
  manifest: {
    nodes: [
      {
        key: "work",
        kind: "Changed",
        before: { timeout: "01:00:00" },
        after: { timeout: "02:00:00" },
      },
    ],
    variables: [],
    policies: [],
    settings: [],
  },
  source: {
    path: "workflow.cs",
    before: "<script>original</script>\r\n\u03bb",
    after: "modified\n",
    changed: true,
  },
}
const respond = (data: unknown) =>
  get.mockResolvedValue({ data, response: new Response() })
const respondDiff = (value: unknown = diff) => respond(JSON.stringify(value))

describe("version history and diff trust boundaries", () => {
  beforeEach(() => vi.resetAllMocks())
  it("binds exact workflow, explicit 50-row cursor and abort signal, preserving immutable provenance", async () => {
    respond(page)
    const signal = new AbortController().signal
    expect(await getVersions(workflowId, "opaque-cursor", signal)).toEqual(page)
    expect(get).toHaveBeenCalledWith(
      "/api/v1/workflows/{team}/{type}/{workflow}/versions",
      {
        params: {
          path: { team: "team", type: "type", workflow: "workflow" },
          query: { limit: 50, cursor: "opaque-cursor" },
        },
        signal,
      }
    )
    get.mockClear()
    await getVersions(workflowId, null)
    expect(get.mock.calls[0]![1].params.query).toEqual({ limit: 50 })
  })
  it.each([
    {
      ...page,
      items: [{ ...page.items[0], workflowId: "foreign/type/workflow" }],
    },
    { ...page, items: [page.items[0], page.items[0]] },
    {
      ...page,
      items: [
        {
          ...page.items[0],
          registration: { ...registration, registeredAt: "not-a-time" },
        },
      ],
    },
    {
      ...page,
      items: [
        {
          ...page.items[0],
          registration: { ...registration, gitCommit: "../../path" },
        },
      ],
    },
    { ...page, nextCursor: "partial-page-cursor" },
    { ...page, latestVersionId: "active" },
  ])(
    "rejects inconsistent history identity, provenance and pagination",
    async (body) => {
      respond(body)
      await expect(getVersions(workflowId, null)).rejects.toThrow()
    }
  )
  it("rejects a repeated cursor on a full page", async () => {
    respond({
      ...page,
      items: Array.from({ length: 50 }, (_, index) => ({
        ...page.items[0],
        versionId: `019ed132-2f44-7000-8000-${String(index).padStart(12, "0")}`,
      })),
      nextCursor: "repeated",
    })
    await expect(getVersions(workflowId, "repeated")).rejects.toThrow(
      "repeated version cursor"
    )
  })
  it("passes exact source text and version-pair identities through a text response parser", async () => {
    respondDiff()
    const signal = new AbortController().signal
    expect(await getVersionDiff(workflowId, from, to, signal)).toEqual(diff)
    expect(get).toHaveBeenCalledWith(
      "/api/v1/workflows/{team}/{type}/{workflow}/diff",
      {
        params: {
          path: { team: "team", type: "type", workflow: "workflow" },
          query: { from, to },
        },
        signal,
        parseAs: "text",
      }
    )
  })
  it.each([
    { ...diff, workflowId: "foreign/type/workflow" },
    { ...diff, fromVersionId: to, toVersionId: from },
    { ...diff, source: { ...diff.source, changed: false } },
    { ...diff, source: { ...diff.source, path: "../workflow.cs" } },
    {
      ...diff,
      manifest: {
        ...diff.manifest,
        nodes: [diff.manifest.nodes[0], diff.manifest.nodes[0]],
      },
    },
    {
      ...diff,
      manifest: {
        ...diff.manifest,
        nodes: [{ key: "work", kind: "Added", before: {}, after: {} }],
      },
    },
    {
      ...diff,
      manifest: {
        ...diff.manifest,
        nodes: [{ key: "work", kind: "Removed", before: {}, after: {} }],
      },
    },
    {
      ...diff,
      manifest: {
        ...diff.manifest,
        nodes: [{ key: "work", kind: "Changed", before: null, after: {} }],
      },
    },
    { ...diff, source: { ...diff.source, after: "\u03bb".repeat(524289) } },
  ])(
    "rejects mismatched diff identities, ambiguous snapshots and expanded source",
    async (body) => {
      respondDiff(body)
      await expect(getVersionDiff(workflowId, from, to)).rejects.toThrow()
    }
  )
  it("preserves Int64 manifest defaults without JavaScript rounding, or explicitly rejects unsupported browsers", async () => {
    const body = JSON.stringify({
      ...diff,
      manifest: {
        ...diff.manifest,
        variables: [
          {
            key: "count",
            kind: "Added",
            before: null,
            after: { default: "MAX_LONG" },
          },
        ],
      },
    }).replace('"MAX_LONG"', "9223372036854775807")
    respond(body)
    if (
      typeof JSON.rawJSON === "function" &&
      typeof JSON.isRawJSON === "function"
    ) {
      const parsed = await getVersionDiff(workflowId, from, to)
      expect(JSON.stringify(parsed.manifest.variables[0]!.after)).toContain(
        "9223372036854775807"
      )
    } else {
      await expect(getVersionDiff(workflowId, from, to)).rejects.toThrow(
        "cannot preserve large JSON numbers"
      )
    }
  })
  it.each([null, {}, "", "{"])(
    "rejects absent or malformed diff responses",
    async (body) => {
      respond(body)
      await expect(getVersionDiff(workflowId, from, to)).rejects.toThrow()
    }
  )
  it.each([401, 403, 404, 500, 503])(
    "surfaces HTTP %i without empty-success fallback",
    async (status) => {
      get.mockResolvedValue({
        response: new Response(null, { status }),
        error: { title: "Retained data unavailable" },
      })
      await expect(getVersions(workflowId, null)).rejects.toBeInstanceOf(
        ApiError
      )
      await expect(getVersionDiff(workflowId, from, to)).rejects.toBeInstanceOf(
        ApiError
      )
    }
  )
  it.each(["team/type", "team/type/../extra", "team/type/bad\\name"])(
    "rejects invalid route identity before sending %s",
    async (identity) => {
      await expect(getVersions(identity, null)).rejects.toThrow()
      await expect(getVersionDiff(identity, from, to)).rejects.toThrow()
      expect(get).not.toHaveBeenCalled()
    }
  )
  it("rejects symbolic, uppercase and empty comparison UUIDs before sending", async () => {
    for (const id of [
      "active",
      from.toUpperCase(),
      "00000000-0000-0000-0000-000000000000",
    ]) {
      await expect(getVersionDiff(workflowId, id, to)).rejects.toThrow()
    }
    expect(get).not.toHaveBeenCalled()
  })
})
