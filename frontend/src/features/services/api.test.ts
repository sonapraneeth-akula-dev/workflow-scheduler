import { beforeEach, expect, it, vi } from "vitest"
import { getServiceHistory, serviceSearchSchema } from "./api"
import type { CatalogWorkflow } from "@/features/catalog/api"

const { get } = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { GET: get },
}))
const workflow: CatalogWorkflow = {
  id: "team/release/a",
  team: "team",
  type: "release",
  name: "a",
  activeVersion: null,
  latestVersion: {
    id: "019ed132-2f44-7000-8000-000000000001",
    semVer: "1.0.0",
  },
  metadata: { type: "release", values: {} },
  latestRun: null,
}
function page() {
  return {
    team: "team",
    type: null,
    window: "14d",
    from: "2026-10-05",
    to: "2026-10-19",
    items: [
      {
        workflow,
        days: Array.from({ length: 14 }, (_, index) => ({
          day: `2026-10-${String(5 + index).padStart(2, "0")}`,
          counts: index ? {} : { Succeeded: 1 },
        })),
        runs: [
          {
            id: "019ed132-2f44-7000-8000-000000000002",
            status: "Succeeded",
            createdAt: "2026-10-05T12:00:00Z",
            startedAt: null,
            endedAt: null,
          },
        ],
      },
    ],
    nextCursor: null as string | null,
  }
}
beforeEach(() => vi.resetAllMocks())
function respond(data: unknown) {
  get.mockResolvedValue({ data, response: new Response() })
}

it("uses exact team, optional type, explicit window, bounded page and cancellation", async () => {
  const data = page()
  respond(data)
  const signal = new AbortController().signal
  expect(
    await getServiceHistory("team", undefined, "14d", null, signal)
  ).toEqual(data)
  expect(get).toHaveBeenCalledWith("/api/v1/service-history", {
    params: {
      query: {
        team: "team",
        type: undefined,
        window: "14d",
        limit: 50,
        cursor: undefined,
      },
    },
    signal,
  })
})

it.each([
  (data: ReturnType<typeof page>) => ({ ...data, team: "foreign" }),
  (data: ReturnType<typeof page>) => ({ ...data, type: "foreign" }),
  (data: ReturnType<typeof page>) => ({ ...data, window: "30d" }),
  (data: ReturnType<typeof page>) => ({ ...data, to: data.from }),
  (data: ReturnType<typeof page>) => ({
    ...data,
    items: [{ ...data.items[0], workflow: { ...workflow, team: "foreign" } }],
  }),
  (data: ReturnType<typeof page>) => ({
    ...data,
    items: [{ ...data.items[0], days: [] }],
  }),
  (data: ReturnType<typeof page>) => ({
    ...data,
    items: [{ ...data.items[0], days: data.items[0]!.days.toReversed() }],
  }),
  (data: ReturnType<typeof page>) => ({
    ...data,
    items: [
      {
        ...data.items[0],
        days: data.items[0]!.days.map((day) => ({
          ...day,
          counts: { Succeeded: -1 },
        })),
      },
    ],
  }),
  (data: ReturnType<typeof page>) => ({
    ...data,
    items: [
      {
        ...data.items[0],
        days: data.items[0]!.days.map((day) => ({
          ...day,
          counts: { Unknown: 1 },
        })),
      },
    ],
  }),
  (data: ReturnType<typeof page>) => ({
    ...data,
    items: [
      {
        ...data.items[0],
        days: data.items[0]!.days.map((day) => ({
          ...day,
          counts: { Succeeded: Number.MAX_SAFE_INTEGER },
        })),
      },
    ],
  }),
  (data: ReturnType<typeof page>) => ({
    ...data,
    items: [...data.items, ...data.items],
  }),
  (data: ReturnType<typeof page>) => ({
    ...data,
    items: [],
    nextCursor: "next",
  }),
])(
  "rejects inconsistent scopes, periods, days, counts and cursors",
  async (change) => {
    respond(change(page()))
    await expect(
      getServiceHistory("team", undefined, "14d", null)
    ).rejects.toThrow()
  }
)

it("validates explicit type, continued cursor and Unicode code-point order", async () => {
  const data = { ...page(), type: "release", nextCursor: "next" }
  respond(data)
  await expect(
    getServiceHistory("team", "release", "14d", {
      cursor: "next",
      afterId: "team/release/0",
    })
  ).rejects.toThrow()
  respond({
    ...data,
    items: [
      {
        ...data.items[0],
        workflow: { ...workflow, id: "team/release/\uE000", name: "\uE000" },
      },
      {
        ...data.items[0],
        workflow: {
          ...workflow,
          id: "team/release/\u{10000}",
          name: "\u{10000}",
        },
      },
    ],
  })
  await expect(
    getServiceHistory("team", "release", "14d", null)
  ).resolves.toHaveProperty("items.length", 2)
  respond({
    ...data,
    items: [{ ...data.items[0], workflow: { ...workflow, type: "foreign" } }],
  })
  await expect(
    getServiceHistory("team", "release", "14d", null)
  ).rejects.toThrow()
})

it.each([
  { id: "not-a-run-id" },
  { status: "Unknown" },
  { status: "Failed" },
  { createdAt: "2026-10-04T23:59:59Z" },
  { createdAt: "2026-10-19T00:00:00Z" },
  { createdAt: "2026-10-05T12:00:00.0000001Z" },
  { startedAt: "invalid" },
])("rejects invalid or inconsistent run instances: %j", async (change) => {
  const data = page()
  respond({
    ...data,
    items: [
      {
        ...data.items[0],
        runs: [{ ...data.items[0]!.runs[0]!, ...change }],
      },
    ],
  })
  await expect(
    getServiceHistory("team", undefined, "14d", null)
  ).rejects.toThrow()
})

it("requires exact bounded counts, unique IDs and newest-first microsecond/UUID order", async () => {
  const data = page()
  const item = data.items[0]!
  const original = item.runs[0]!
  item.days[0]!.counts = { Succeeded: 2 }
  item.runs = [
    {
      ...original,
      id: "019ed132-2f44-7000-8000-000000000001",
      createdAt: "2026-10-05T12:00:00.000002Z",
    },
    {
      ...original,
      id: "019ed132-2f44-7000-8000-000000000002",
      createdAt: "2026-10-05T17:30:00.000001+05:30",
    },
  ]
  respond(data)
  await expect(
    getServiceHistory("team", undefined, "14d", null)
  ).resolves.toHaveProperty("items.0.runs.length", 2)
  item.runs.reverse()
  respond(data)
  await expect(
    getServiceHistory("team", undefined, "14d", null)
  ).rejects.toThrow()
  item.runs[1]!.createdAt = item.runs[0]!.createdAt
  respond(data)
  await expect(
    getServiceHistory("team", undefined, "14d", null)
  ).resolves.toBeDefined()
  item.runs.reverse()
  respond(data)
  await expect(
    getServiceHistory("team", undefined, "14d", null)
  ).rejects.toThrow()
  item.runs = [original, original]
  respond(data)
  await expect(
    getServiceHistory("team", undefined, "14d", null)
  ).rejects.toThrow()
  item.runs = []
  respond(data)
  await expect(
    getServiceHistory("team", undefined, "14d", null)
  ).rejects.toThrow()
  item.days[0]!.counts = {}
  respond(data)
  await expect(
    getServiceHistory("team", undefined, "14d", null)
  ).resolves.toBeDefined()
})

it("accepts 50 recent instances with larger full-window counts but rejects over-cap or incomplete lists", async () => {
  const data = page()
  const item = data.items[0]!
  const original = item.runs[0]!
  item.days[0]!.counts = { Succeeded: 80 }
  item.runs = Array.from({ length: 51 }, (_, index) => ({
    ...original,
    id: `019ed132-2f44-7000-8000-${(100 - index).toString(16).padStart(12, "0")}`,
  }))
  respond(data)
  await expect(
    getServiceHistory("team", undefined, "14d", null)
  ).rejects.toThrow()
  item.runs.pop()
  respond(data)
  await expect(
    getServiceHistory("team", undefined, "14d", null)
  ).resolves.toHaveProperty("items.0.runs.length", 50)
  item.runs.pop()
  respond(data)
  await expect(
    getServiceHistory("team", undefined, "14d", null)
  ).rejects.toThrow()
})

it.each([401, 403, 503])(
  "surfaces HTTP %i without an empty or healthy fallback",
  async (status) => {
    get.mockResolvedValue({
      response: new Response(null, { status }),
      error: { title: "Service history unavailable" },
    })
    await expect(
      getServiceHistory("team", undefined, "14d", null)
    ).rejects.toMatchObject({ status })
  }
)

it("validates copied format and period choices", () => {
  expect(serviceSearchSchema.parse({})).toEqual({
    window: "14d",
    view: "cards",
    groupBy: "type",
  })
  expect(serviceSearchSchema.safeParse({ window: "365d" }).success).toBe(false)
  expect(serviceSearchSchema.safeParse({ view: "grid" }).success).toBe(false)
})

it.each(["7d", "14d", "30d", "90d"])(
  "normalizes saved %s periods to the fixed two-week view",
  (window) => {
    expect(serviceSearchSchema.parse({ window }).window).toBe("14d")
  }
)

it("restores metadata grouping without conflating the built-in type and a metadata key named type", () => {
  expect(
    serviceSearchSchema.parse({ groupBy: "metadata", groupKey: "type" })
  ).toMatchObject({
    groupBy: "metadata",
    groupKey: "type",
  })
  expect(serviceSearchSchema.safeParse({ groupBy: "metadata" }).success).toBe(
    false
  )
  expect(
    serviceSearchSchema.safeParse({ groupBy: "metadata", groupKey: "" }).success
  ).toBe(false)
  expect(serviceSearchSchema.safeParse({ groupBy: "foreign" }).success).toBe(
    false
  )
})
