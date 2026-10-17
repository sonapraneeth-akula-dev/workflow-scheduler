import { beforeEach, describe, expect, it, vi } from "vitest"
import {
  createSubscription,
  deleteSubscription,
  getSubscriptions,
  subscriptionRequestSchema,
  subscriptionScopeSchema,
} from "./api"

const { get, post, remove } = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  remove: vi.fn(),
}))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  publicApi: { GET: get, POST: post, DELETE: remove },
}))
const resource = { scope: "workflow" as const, scopeId: "team/type/workflow" }
const request = {
  ...resource,
  events: ["Completed" as const],
  channel: "Email" as const,
  target: "subscriber@example.test",
  secretRef: null,
}
const id = "019ed132-2f44-7000-8000-000000000002"
const older = "019ed132-2f44-7000-8000-000000000001"
const row = { ...request, id, createdAt: "2026-10-17T10:00:00Z" }
const ok = (data: unknown, status = 200) => ({
  response: new Response(null, { status }),
  data,
})

describe("subscription public API boundary", () => {
  beforeEach(() => vi.resetAllMocks())

  it.each([
    { scope: "workflow", scopeId: "team/type/workflow" },
    { scope: "type", scopeId: "team/type" },
    { scope: "schedule", scopeId: id },
  ])("accepts canonical scoped identities %j", (scope) => {
    expect(subscriptionScopeSchema.parse(scope)).toEqual(scope)
  })
  it.each([
    { scope: "team", scopeId: "team" },
    { scope: "workflow", scopeId: "../type/workflow" },
    { scope: "workflow", scopeId: "team/type/.." },
    { scope: "workflow", scopeId: "team/type/ " },
    { scope: "workflow", scopeId: "team/type/\u0000" },
    { scope: "workflow", scopeId: `team/type/${"a".repeat(129)}` },
    { scope: "type", scopeId: "team/type/workflow" },
    { scope: "schedule", scopeId: id.toUpperCase() },
    { scope: "schedule", scopeId: "00000000-0000-0000-0000-000000000000" },
    { scope: "schedule", scopeId: "../other" },
  ])("rejects invalid identities before HTTP %j", (scope) => {
    expect(subscriptionScopeSchema.safeParse(scope).success).toBe(false)
    expect(get).not.toHaveBeenCalled()
  })

  it.each([
    { channel: "Email", target: "subscriber@example.test", secretRef: null },
    {
      channel: "Teams",
      target: "secret://notifications/teams",
      secretRef: null,
    },
    { channel: "Teams", target: "notifications/teams", secretRef: null },
    {
      channel: "Webhook",
      target: "https://notify.example.test/hook",
      secretRef: "secret://notifications/signing",
    },
    {
      channel: "Webhook",
      target: "http://127.0.0.1:8765/hook",
      secretRef: "signing",
    },
    {
      channel: "Webhook",
      target: "http://[::1]:8765/hook",
      secretRef: "signing",
    },
  ])("creates each supported safe configuration %j", async (destination) => {
    const input = subscriptionRequestSchema.parse({
      ...request,
      ...destination,
    })
    post.mockResolvedValue(ok({ ...row, ...input }, 201))
    expect(await createSubscription(input)).toEqual({ ...row, ...input })
    expect(post).toHaveBeenCalledWith("/api/v1/subscriptions", { body: input })
  })
  it("canonicalizes the URL and event order before sending and verifies the exact receipt", async () => {
    const input = {
      ...request,
      channel: "Webhook" as const,
      target: "HTTPS://NOTIFY.EXAMPLE.TEST",
      secretRef: "signing",
      events: ["Failed" as const, "Completed" as const],
    }
    const expected = {
      ...input,
      target: "https://notify.example.test/",
      events: ["Completed", "Failed"],
    }
    post.mockResolvedValue(ok({ ...row, ...expected }, 201))
    await createSubscription(input)
    expect(post).toHaveBeenCalledWith("/api/v1/subscriptions", {
      body: expected,
    })
  })
  it.each([
    { channel: "Email", target: "Display <user@example.test>" },
    { channel: "Email", target: "user@example.test,other@example.test" },
    {
      channel: "Email",
      target: "user@example.test\r\nBcc: other@example.test",
    },
    { channel: "Email", secretRef: "signing" },
    {
      channel: "Teams",
      target: "https://workflow.example.test/hook?sig=private",
    },
    { channel: "Teams", target: "secret://Vault/name" },
    { channel: "Teams", target: "secret://vault/../name" },
    { channel: "Teams", target: "secret://vault-/name" },
    {
      channel: "Webhook",
      target: "http://notify.example.test/hook",
      secretRef: "signing",
    },
    {
      channel: "Webhook",
      target: "https://user:private@notify.example.test/hook",
      secretRef: "signing",
    },
    {
      channel: "Webhook",
      target: "https://notify.example.test/hook?sig=private",
      secretRef: "signing",
    },
    {
      channel: "Webhook",
      target: "https://notify.example.test/hook?",
      secretRef: "signing",
    },
    {
      channel: "Webhook",
      target: "https://notify.example.test/hook#private",
      secretRef: "signing",
    },
    {
      channel: "Webhook",
      target: "https://notify.example.test/hook#",
      secretRef: "signing",
    },
    {
      channel: "Webhook",
      target: "https://notify.example.test/hook",
      secretRef: null,
    },
    { events: [] },
    { events: ["Completed", "Completed"] },
    { events: ["Running"] },
    { target: " subscriber@example.test" },
  ])("rejects unsafe drafts before sending %j", (change) => {
    expect(
      subscriptionRequestSchema.safeParse({ ...request, ...change }).success
    ).toBe(false)
    expect(post).not.toHaveBeenCalled()
  })
  it("sends only the exact scope and bounded keyset filters with cancellation", async () => {
    const abort = new AbortController()
    get.mockResolvedValue(
      ok({ items: [{ ...row, id: older }], nextCursor: null })
    )
    await getSubscriptions(resource, id, abort.signal)
    expect(get).toHaveBeenCalledWith("/api/v1/subscriptions", {
      params: { query: { ...resource, limit: 50, cursor: id } },
      signal: abort.signal,
    })
  })
  it("accepts a full descending page with a cursor equal to its last ID", async () => {
    const items = Array.from({ length: 50 }, (_, index) => ({
      ...row,
      id: `019ed132-2f44-7000-8000-${(100 - index).toString().padStart(12, "0")}`,
    }))
    get.mockResolvedValue(ok({ items, nextCursor: items.at(-1)!.id }))
    expect((await getSubscriptions(resource)).items).toHaveLength(50)
  })
  it.each([
    {
      items: [{ ...row, scope: "type", scopeId: "team/type" }],
      nextCursor: null,
    },
    { items: [{ ...row, scopeId: "foreign/type/workflow" }], nextCursor: null },
    { items: [row, row], nextCursor: null },
    { items: [{ ...row, id: older }, row], nextCursor: null },
    { items: [row], nextCursor: id },
    { items: [{ ...row, secretRef: "literal-credential" }], nextCursor: null },
    { items: [{ ...row, private: "unexpected" }], nextCursor: null },
    { items: [row], nextCursor: null, principalId: "unexpected" },
  ])("rejects foreign, malformed or inconsistent pages %j", async (data) => {
    get.mockResolvedValue(ok(data))
    await expect(getSubscriptions(resource)).rejects.toThrow()
  })
  it("rejects rows on the wrong side of the requested cursor", async () => {
    get.mockResolvedValue(ok({ items: [row], nextCursor: null }))
    await expect(getSubscriptions(resource, older)).rejects.toThrow(
      "pagination"
    )
  })
  it.each([
    { scopeId: "foreign/type/workflow" },
    { events: ["Failed"] },
    { channel: "Teams", target: "secret://notifications/teams" },
    { target: "other@example.test" },
    { id: id.toUpperCase() },
  ])("rejects a different creation receipt %j", async (change) => {
    post.mockResolvedValue(ok({ ...row, ...change }, 201))
    await expect(createSubscription(request)).rejects.toThrow()
  })
  it.each([200, 202])(
    "rejects success-shaped creation without HTTP 201 (%i)",
    async (status) => {
      post.mockResolvedValue(ok(row, status))
      await expect(createSubscription(request)).rejects.toThrow("HTTP 201")
    }
  )
  it("rejects success-shaped listing and deletion with the wrong status", async () => {
    get.mockResolvedValue(ok({ items: [], nextCursor: null }, 201))
    await expect(getSubscriptions(resource)).rejects.toThrow("HTTP 200")
    remove.mockResolvedValue(ok(undefined, 200))
    await expect(deleteSubscription(id)).rejects.toThrow("HTTP 204")
  })
  it("requires an exact canonical owner-delete ID and HTTP 204, without a payload", async () => {
    remove.mockResolvedValue(ok(undefined, 204))
    await deleteSubscription(id)
    expect(remove).toHaveBeenCalledWith("/api/v1/subscriptions/{id}", {
      params: { path: { id } },
    })
    await expect(deleteSubscription("../foreign")).rejects.toThrow()
    expect(remove).toHaveBeenCalledTimes(1)
  })
  it.each([400, 401, 403, 404, 409, 500, 503])(
    "preserves explicit HTTP %i errors without retrying",
    async (status) => {
      const failure = {
        response: new Response(null, { status }),
        error: { title: "Subscription unavailable" },
      }
      get.mockResolvedValue(failure)
      post.mockResolvedValue(failure)
      remove.mockResolvedValue(failure)
      for (const result of [
        getSubscriptions(resource),
        createSubscription(request),
        deleteSubscription(id),
      ])
        await expect(result).rejects.toMatchObject({
          status,
          message: "Subscription unavailable",
        })
      expect(get).toHaveBeenCalledTimes(1)
      expect(post).toHaveBeenCalledTimes(1)
      expect(remove).toHaveBeenCalledTimes(1)
    }
  )
})
