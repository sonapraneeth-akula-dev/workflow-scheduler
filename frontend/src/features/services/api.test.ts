import { beforeEach, expect, it, vi } from "vitest"
import {
  getServiceHistory,
  healthState,
  healthSummary,
  serviceSearchSchema,
} from "./api"
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
    window: "7d",
    from: "2026-10-12",
    to: "2026-10-19",
    items: [
      {
        workflow,
        days: Array.from({ length: 7 }, (_, index) => ({
          day: `2026-10-${String(12 + index).padStart(2, "0")}`,
          counts: index ? {} : { Succeeded: 1 },
        })),
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
    await getServiceHistory("team", undefined, "7d", null, signal)
  ).toEqual(data)
  expect(get).toHaveBeenCalledWith("/api/v1/service-history", {
    params: {
      query: {
        team: "team",
        type: undefined,
        window: "7d",
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
      getServiceHistory("team", undefined, "7d", null)
    ).rejects.toThrow()
  }
)

it("validates explicit type, continued cursor and Unicode code-point order", async () => {
  const data = { ...page(), type: "release", nextCursor: "next" }
  respond(data)
  await expect(
    getServiceHistory("team", "release", "7d", {
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
    getServiceHistory("team", "release", "7d", null)
  ).resolves.toHaveProperty("items.length", 2)
  respond({
    ...data,
    items: [{ ...data.items[0], workflow: { ...workflow, type: "foreign" } }],
  })
  await expect(
    getServiceHistory("team", "release", "7d", null)
  ).rejects.toThrow()
})

it("keeps no-run days neutral and never paints mixed or unfinished outcomes as healthy", () => {
  expect(healthState({})).toBe("NoRuns")
  expect(healthSummary({})).toBe("No runs")
  expect(healthState({ Succeeded: 5 })).toBe("Succeeded")
  expect(healthState({ Succeeded: 5, Running: 1 })).toBe("Running")
  expect(healthState({ Succeeded: 5, Aborted: 1 })).toBe("Aborted")
  expect(healthState({ Succeeded: 5, TimedOut: 1, Running: 1 })).toBe("Failed")
  expect(healthSummary({ Succeeded: 5, Failed: 1 })).toBe(
    "5 succeeded, 1 failed"
  )
})

it.each([401, 403, 503])(
  "surfaces HTTP %i without an empty or healthy fallback",
  async (status) => {
    get.mockResolvedValue({
      response: new Response(null, { status }),
      error: { title: "Service history unavailable" },
    })
    await expect(
      getServiceHistory("team", undefined, "7d", null)
    ).rejects.toMatchObject({ status })
  }
)

it("validates copied format and period choices", () => {
  expect(serviceSearchSchema.parse({})).toEqual({
    window: "30d",
    view: "cards",
    groupBy: "type",
  })
  expect(serviceSearchSchema.safeParse({ window: "365d" }).success).toBe(false)
  expect(serviceSearchSchema.safeParse({ view: "grid" }).success).toBe(false)
})

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
