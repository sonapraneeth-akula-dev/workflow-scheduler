import { beforeEach, expect, it, vi } from "vitest"
import {
  getRecurringJobs,
  recurringJobsSearchSchema,
} from "./recurring-jobs-api"

const { get } = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { GET: get },
}))
const job = {
  id: "019ed132-2f44-7000-8000-000000000001",
  seriesId: "019ed132-2f44-7000-8000-000000000002",
  workflowId: "team/regression/logistic",
  name: "Recurring experiment",
  origin: "Code",
  sourcePath: "workflow.schedules.json",
  gitCommit: "a".repeat(40),
  timezone: "Etc/UTC",
  cron: null,
  interval: "00:05:00",
  versionPin: null,
  overlap: "Skip",
  catchUp: "Latest",
  variables: { seed: 42 },
  paused: false,
  rowVersion: 1,
  nextFireAt: "2026-10-20T12:00:00.123456Z",
}
function page() {
  return { items: [{ ...job }], nextCursor: null as string | null }
}
function respond(value: unknown) {
  get.mockResolvedValue({
    data: JSON.stringify(value),
    response: new Response(),
  })
}
beforeEach(() => vi.resetAllMocks())

it("requests one bounded global or team/type schedule page with cancellation and lossless JSON parsing", async () => {
  const signal = new AbortController().signal
  for (const search of [{}, { team: "team", type: "regression" }]) {
    const data = page()
    respond(data)
    expect(await getRecurringJobs(search, null, signal)).toEqual(data)
    expect(get).toHaveBeenLastCalledWith("/api/v1/schedules", {
      params: { query: { ...search, limit: 50, cursor: undefined } },
      parseAs: "text",
      signal,
    })
  }
})

it("accepts paused User cron schedules and legacy UUIDs without requiring a next fire", async () => {
  const data = {
    items: [
      {
        ...job,
        id: "11111111-1111-4111-8111-111111111111",
        origin: "User",
        sourcePath: null,
        gitCommit: null,
        cron: "0 9 * * 1-5",
        interval: null,
        paused: true,
        nextFireAt: null,
      },
    ],
    nextCursor: null,
  }
  respond(data)
  expect(await getRecurringJobs({}, null)).toEqual(data)
})

it.each([
  (data: ReturnType<typeof page>) => ({
    ...data,
    items: [{ ...job, workflowId: "other/regression/logistic" }],
  }),
  (data: ReturnType<typeof page>) => ({
    ...data,
    items: [{ ...job, workflowId: "team/other/logistic" }],
  }),
  (data: ReturnType<typeof page>) => ({ ...data, items: [job, job] }),
  (data: ReturnType<typeof page>) => ({
    ...data,
    items: [{ ...job, id: "019ed132-2f44-7000-8000-000000000003" }, job],
  }),
  (data: ReturnType<typeof page>) => ({
    ...data,
    items: [{ ...job, workflowId: "bad" }],
  }),
  (data: ReturnType<typeof page>) => ({
    ...data,
    items: [{ ...job, cron: "* * * * *" }],
  }),
  (data: ReturnType<typeof page>) => ({
    ...data,
    items: [{ ...job, interval: null }],
  }),
  (data: ReturnType<typeof page>) => ({
    ...data,
    items: [{ ...job, interval: "00:00:00" }],
  }),
  (data: ReturnType<typeof page>) => ({
    ...data,
    items: [{ ...job, interval: "not a duration" }],
  }),
  (data: ReturnType<typeof page>) => ({
    ...data,
    items: [],
    nextCursor: "next",
  }),
])(
  "rejects out-of-scope, malformed or inconsistent recurring jobs",
  async (change) => {
    respond(change(page()))
    await expect(
      getRecurringJobs({ team: "team", type: "regression" }, null)
    ).rejects.toThrow()
  }
)

it("rejects repeated cursors and nonadvancing page boundaries", async () => {
  const cursor = { cursor: "next", afterId: job.id }
  respond({ items: [], nextCursor: "next" })
  await expect(getRecurringJobs({}, cursor)).rejects.toThrow()
  respond(page())
  await expect(getRecurringJobs({}, cursor)).rejects.toThrow()
})

it.each([{ type: "regression" }, { team: "bad/team" }, { type: "" }])(
  "rejects invalid URL filters before sending requests",
  async (search) => {
    expect(recurringJobsSearchSchema.safeParse(search).success).toBe(false)
    await expect(getRecurringJobs(search, null)).rejects.toThrow()
    expect(get).not.toHaveBeenCalled()
  }
)

it("surfaces API denial instead of returning an empty success", async () => {
  get.mockResolvedValue({
    response: new Response(null, { status: 403 }),
    error: { title: "Viewer permission revoked" },
  })
  await expect(getRecurringJobs({}, null)).rejects.toThrow(
    "Viewer permission revoked"
  )
})

it("does not silently round unsafe variable numbers in the reused schedule projection", async () => {
  const json = JSON.stringify(page()).replace(
    '"seed":42',
    '"seed":9007199254740993'
  )
  get.mockResolvedValue({ response: new Response(), data: json })
  expect(JSON.stringify(await getRecurringJobs({}, null))).toBe(json)
})
