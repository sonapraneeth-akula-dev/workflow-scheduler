import { afterEach, beforeEach, expect, it, vi } from "vitest"
import {
  deploymentHistorySchema,
  deploymentSnapshotSchema,
  getDeploymentHistory,
  getDeploymentSnapshot,
} from "./api"
import { snapshot, history } from "./testFixture"
import { accessToken } from "@/lib/auth/session"
import { ApiError } from "@/lib/api/client"

vi.mock("@/lib/auth/session", () => ({ accessToken: vi.fn() }))
const fetcher = vi.fn<typeof fetch>()
beforeEach(() => {
  vi.resetAllMocks()
  vi.stubGlobal("fetch", fetcher)
  vi.mocked(accessToken).mockResolvedValue("test-access-token")
})
afterEach(() => vi.unstubAllGlobals())

it("uses authenticated same-origin no-store read-only endpoints with cancellation", async () => {
  const actual = snapshot()
  const reported = history()
  fetcher
    .mockResolvedValueOnce(new Response(JSON.stringify(actual)))
    .mockResolvedValueOnce(new Response(JSON.stringify(reported)))
  const signal = new AbortController().signal
  expect(await getDeploymentSnapshot(signal)).toEqual(actual)
  expect(await getDeploymentHistory(signal)).toEqual(reported)
  expect(fetcher.mock.calls.map(([path]) => path)).toEqual([
    "/api/v1/admin/system/deployments",
    "/api/v1/admin/deployment-activity",
  ])
  for (const [, options] of fetcher.mock.calls) {
    expect(options).toMatchObject({ cache: "no-store", signal })
    expect(options?.method).toBeUndefined()
    expect(new Headers(options?.headers).get("Authorization")).toBe(
      "Bearer test-access-token"
    )
  }
})

it("preserves explicit unavailable and access-denied failures without healthy fallbacks", async () => {
  for (const status of [401, 403, 503]) {
    fetcher.mockImplementation(
      async () =>
        new Response('{"title":"Observation unavailable"}', { status })
    )
    await expect(getDeploymentSnapshot()).rejects.toBeInstanceOf(ApiError)
    await expect(getDeploymentHistory()).rejects.toMatchObject({ status })
  }
  fetcher.mockResolvedValue(new Response("{}"))
  await expect(getDeploymentSnapshot()).rejects.toThrow()
})

it("rejects missing workloads, duplicate identities and broken operation sequences", () => {
  const actual = snapshot()
  expect(deploymentSnapshotSchema.safeParse(actual).success).toBe(true)
  expect(
    deploymentSnapshotSchema.safeParse({ ...actual, workloads: [] }).success
  ).toBe(false)
  expect(
    deploymentSnapshotSchema.safeParse({
      ...actual,
      pods: [...actual.pods, ...actual.pods],
    }).success
  ).toBe(false)
  const reported = history()
  expect(deploymentHistorySchema.safeParse(reported).success).toBe(true)
  reported.operations[0]!.events[0]!.sequence = 2
  expect(deploymentHistorySchema.safeParse(reported).success).toBe(false)
})
