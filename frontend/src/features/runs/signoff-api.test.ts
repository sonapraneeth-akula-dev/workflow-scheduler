import { beforeEach, describe, expect, it, vi } from "vitest"
import { changeSignOff, getSignOff } from "./signoff-api"

const { get, post } = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { GET: get, POST: post },
}))
const runId = "019ed132-2f44-7000-8000-000000000001"
const reportId = "019ed132-2f44-7000-8000-000000000002"
const event = {
  revision: "1",
  action: "SignOff",
  reason: "Reviewed",
  actorId: "11111111-1111-4111-8111-111111111111",
  actorName: "Reviewer",
  at: "2026-10-21T00:00:00Z",
}
const data = {
  runId,
  reportId: null,
  workflowId: "team/type/workflow",
  versionId: "11111111-1111-4111-8111-111111111111",
  isTerminal: true,
  state: { revision: "1", status: "SignedOff", latest: event },
  history: [event],
  nextBefore: null,
}
function response(value: unknown = data, etag = '"1"') {
  return {
    response: new Response(null, { headers: { ETag: etag } }),
    data: value,
  }
}

describe("sign-off API trust boundaries", () => {
  beforeEach(() => vi.resetAllMocks())
  it("reads the exact target and preserves decimal revisions and ETag", async () => {
    get.mockResolvedValue(response())
    expect(await getSignOff({ runId })).toEqual(data)
    expect(get).toHaveBeenCalledWith("/api/v1/runs/{id}/signoff", {
      params: {
        path: { id: runId },
        query: { before: undefined, limit: "20" },
      },
      signal: undefined,
    })
    get.mockResolvedValue(response({ ...data, reportId }))
    expect((await getSignOff({ runId, reportId })).reportId).toBe(reportId)
    expect(get).toHaveBeenLastCalledWith(
      "/api/v1/runs/{id}/reports/{reportId}/signoff",
      expect.objectContaining({
        params: expect.objectContaining({ path: { id: runId, reportId } }),
      })
    )
  })
  it.each(["", " ", "\n\t", "x".repeat(2001), "bad\0reason"])(
    "rejects invalid reason before network access",
    async (reason) => {
      await expect(
        changeSignOff({ runId }, "0", "SignOff", reason)
      ).rejects.toThrow()
      expect(post).not.toHaveBeenCalled()
    }
  )
  it.each(["01", "-1", "9223372036854775808", ""])(
    "rejects invalid revision %s before network access",
    async (revision) => {
      await expect(
        changeSignOff({ runId }, revision, "SignOff", "Reviewed")
      ).rejects.toThrow()
      expect(post).not.toHaveBeenCalled()
    }
  )
  it("writes only reason with a strong current If-Match header", async () => {
    post.mockResolvedValue(response())
    expect(await changeSignOff({ runId }, "0", "SignOff", "Reviewed")).toEqual(
      data
    )
    expect(post).toHaveBeenCalledWith("/api/v1/runs/{id}/signoff", {
      params: { path: { id: runId }, header: { "If-Match": '"0"' } },
      body: { reason: "Reviewed" },
    })
    const revoked = {
      ...event,
      revision: "2",
      action: "Revoke",
      reason: "Regression",
    }
    post.mockResolvedValue(
      response(
        {
          ...data,
          reportId,
          state: { revision: "2", status: "Revoked", latest: revoked },
          history: [revoked, event],
        },
        '"2"'
      )
    )
    await changeSignOff({ runId, reportId }, "1", "Revoke", "Regression")
    expect(post).toHaveBeenLastCalledWith(
      "/api/v1/runs/{id}/reports/{reportId}/signoff/revoke",
      {
        params: {
          path: { id: runId, reportId },
          header: { "If-Match": '"1"' },
        },
        body: { reason: "Regression" },
      }
    )
  })
  it.each([
    { ...data, runId: reportId },
    { ...data, reportId },
    { ...data, state: { ...data.state, revision: "2" } },
    { ...data, state: { ...data.state, status: "Revoked" } },
    { ...data, history: [] },
    { ...data, history: [{ ...event, revision: "2" }] },
    { ...data, nextBefore: "2" },
  ])("rejects inconsistent target, state or history", async (value) => {
    get.mockResolvedValue(response(value))
    await expect(getSignOff({ runId })).rejects.toThrow()
  })
  it("rejects stale response ETags and does not confuse sign-off conflicts with schedules", async () => {
    get.mockResolvedValue(response(data, '"0"'))
    await expect(getSignOff({ runId })).rejects.toThrow("inconsistent")
    post.mockResolvedValue({
      response: new Response(null, { status: 412 }),
      error: { title: "Sign-off changed" },
    })
    await expect(
      changeSignOff({ runId }, "0", "SignOff", "Reviewed")
    ).rejects.toThrow("This sign-off changed elsewhere")
    expect(post).toHaveBeenCalledOnce()
  })

  it("preserves Int64 revisions beyond JavaScript's safe integer range while paging", async () => {
    const revision = 9223372036854775806n
    const history = Array.from({ length: 20 }, (_, index) => ({
      ...event,
      revision: String(revision - BigInt(index)),
    }))
    const large = {
      ...data,
      state: { ...data.state, revision: String(revision), latest: history[0] },
      history,
      nextBefore: history.at(-1)!.revision,
    }
    get.mockResolvedValue(response(large, `"${revision}"`))
    const result = await getSignOff({ runId })
    expect(result.state.revision).toBe("9223372036854775806")
    expect(result.nextBefore).toBe("9223372036854775787")
  })
})
