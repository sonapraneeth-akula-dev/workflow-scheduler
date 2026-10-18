import { beforeEach, describe, expect, it, vi } from "vitest"
import { publicApi } from "@/lib/api/client"
import {
  getSystemEvents,
  systemEventsFilters,
  type SystemEventsPage,
} from "./system-events-api"

vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { GET: vi.fn() },
}))
const runId = "019ed132-2f44-7000-8000-000000000001"
const page: SystemEventsPage = {
  runId,
  workflowId: "team/type/workflow",
  versionId: "019ed132-2f44-7000-8000-000000000002",
  lastSeq: "9223372036854775807",
  nextCursor: null,
  events: [
    {
      seq: "9007199254740993",
      at: "2026-10-18T00:00:00Z",
      entity: 2,
      key: "node/task",
      state: 2,
      attempt: 2,
      agentId: "019ed132-2f44-7000-8000-000000000003",
      selectionReason: "LeastLoaded",
      plannedAgentId: "019ed132-2f44-7000-8000-000000000004",
      waitingReason: null,
      failureCategory: null,
      progress: null,
    },
  ],
}
function respond(data: unknown = page) {
  vi.mocked(publicApi.GET).mockResolvedValue({
    response: new Response(),
    data,
  } as Awaited<ReturnType<typeof publicApi.GET>>)
}
describe("system events public API", () => {
  beforeEach(() => vi.resetAllMocks())
  it("preserves Int64 digits, exact filters, bounded pagination and abort signal", async () => {
    respond()
    const signal = new AbortController().signal
    const filters = { node: "node", task: "node/task", attempt: "2" }
    const result = await getSystemEvents(runId, filters, undefined, signal)
    expect(result.events[0]?.seq).toBe("9007199254740993")
    expect(result.lastSeq).toBe("9223372036854775807")
    expect(publicApi.GET).toHaveBeenCalledWith(
      "/api/v1/runs/{id}/system-events",
      {
        params: {
          path: { id: runId },
          query: { ...filters, limit: "10", cursor: undefined },
        },
        signal,
      }
    )
  })
  it.each([
    { node: "" },
    { node: "a/b" },
    { node: "a\nb" },
    { task: "task" },
    { task: "node/task/more" },
    { node: "other", task: "node/task" },
    { attempt: "01" },
    { attempt: "0" },
    { attempt: "32768" },
    { attempt: "1e2" },
  ])("rejects malformed filters before HTTP: %j", async (filters) => {
    expect(systemEventsFilters.safeParse(filters).success).toBe(false)
    await expect(getSystemEvents(runId, filters)).rejects.toThrow()
    expect(publicApi.GET).not.toHaveBeenCalled()
  })
  it.each([
    { ...page, runId: page.versionId },
    { ...page, lastSeq: "01" },
    { ...page, lastSeq: "9223372036854775808" },
    { ...page, lastSeq: "1" },
    { ...page, events: [page.events[0], page.events[0]] },
    {
      ...page,
      events: [{ ...page.events[0], selectionReason: "private-secret" }],
    },
    { ...page, events: [{ ...page.events[0], state: 3 }] },
    {
      ...page,
      events: [{ ...page.events[0], failureReason: "private-secret" }],
    },
    { ...page, events: [{ ...page.events[0], agentId: null }] },
    { ...page, events: [{ ...page.events[0], selectionReason: null }] },
  ])(
    "rejects inconsistent identity/order/metadata or unexpected secret-bearing fields",
    async (invalid) => {
      respond(invalid)
      await expect(getSystemEvents(runId, {})).rejects.toThrow()
    }
  )
  it("does not accept a page that ignores the requested attempt filter", async () => {
    respond()
    await expect(getSystemEvents(runId, { attempt: "1" })).rejects.toThrow(
      /inconsistent/
    )
  })
})
