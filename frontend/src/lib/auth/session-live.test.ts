import { afterEach, beforeEach, expect, it, vi } from "vitest"

const auth = vi.hoisted(() => ({
  initialize: vi.fn(async () => undefined),
  redirect: vi.fn(async () => undefined),
  login: vi.fn(async () => ({ account: { homeAccountId: "new-account" } })),
  logout: vi.fn(async () => undefined),
  select: vi.fn(),
  reset: vi.fn(async () => undefined),
}))
vi.mock("@azure/msal-browser", () => ({
  PublicClientApplication: class {
    initialize = auth.initialize
    handleRedirectPromise = auth.redirect
    loginPopup = auth.login
    logoutPopup = auth.logout
    setActiveAccount = auth.select
  },
}))
vi.mock("@/lib/live/subscriptions", () => ({
  liveSubscriptions: { reset: auth.reset },
}))

beforeEach(() => {
  vi.clearAllMocks()
  vi.stubEnv("VITE_AUTH_MODE", undefined)
  vi.stubEnv("VITE_ENTRA_TENANT_ID", "60d24b19-f65a-4ed4-96cc-9174bfcacabc")
  vi.stubEnv("VITE_ENTRA_SPA_CLIENT_ID", "562b0870-4522-43da-8587-41e9b788f309")
  vi.stubEnv("VITE_ENTRA_API_SCOPE", "api://kardia/read")
})
afterEach(() => {
  vi.resetModules()
  vi.unstubAllEnvs()
})

it("closes and clears live scopes before switching the active signed-in account", async () => {
  const { signIn } = await import("./session")
  await signIn()
  expect(auth.login).toHaveBeenCalledOnce()
  expect(auth.reset).toHaveBeenCalledOnce()
  expect(auth.reset.mock.invocationCallOrder[0]).toBeLessThan(
    auth.select.mock.invocationCallOrder[0] ?? 0
  )
  expect(auth.select).toHaveBeenCalledWith({ homeAccountId: "new-account" })
})

it("closes live scopes before starting interactive sign-out", async () => {
  const { signOut } = await import("./session")
  await signOut()
  expect(auth.reset).toHaveBeenCalledOnce()
  expect(auth.logout).toHaveBeenCalledOnce()
  expect(auth.reset.mock.invocationCallOrder[0]).toBeLessThan(
    auth.logout.mock.invocationCallOrder[0] ?? 0
  )
})

it("surfaces connection-cleanup failure rather than silently signing out with active live scopes", async () => {
  auth.reset.mockRejectedValueOnce(new Error("Live cleanup failed"))
  const { signOut } = await import("./session")
  await expect(signOut()).rejects.toThrow("Live cleanup failed")
  expect(auth.logout).not.toHaveBeenCalled()
})
