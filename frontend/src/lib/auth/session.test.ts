import { afterEach, describe, expect, it, vi } from "vitest"

afterEach(() => {
  vi.unstubAllEnvs()
  vi.unstubAllGlobals()
  vi.resetModules()
})

describe("authentication cannot silently bypass Entra", () => {
  it.each(["localhost", "127.0.0.1", "[::1]"])(
    "allows explicit development auth only on %s",
    async (hostname) => {
      vi.stubEnv("VITE_AUTH_MODE", "development")
      vi.stubEnv("DEV", true)
      vi.stubGlobal("location", { hostname })
      const { getSession, accessToken } = await import("./session")
      await expect(getSession()).resolves.toEqual({
        displayName: "Local developer",
        isDevelopment: true,
      })
      await expect(accessToken()).resolves.toBeNull()
    }
  )

  it("rejects a development bypass on nonloopback hosts", async () => {
    vi.stubEnv("VITE_AUTH_MODE", "development")
    vi.stubEnv("DEV", true)
    vi.stubGlobal("location", { hostname: "example.invalid" })
    const { getSession } = await import("./session")
    await expect(getSession()).rejects.toThrow(
      "requires a loopback Vite development server"
    )
  })

  it("rejects a development bypass in a production build", async () => {
    vi.stubEnv("VITE_AUTH_MODE", "development")
    vi.stubEnv("DEV", false)
    vi.stubGlobal("location", { hostname: "localhost" })
    const { accessToken } = await import("./session")
    await expect(accessToken()).rejects.toThrow(
      "requires a loopback Vite development server"
    )
  })

  it("fails closed without configured Entra identities instead of auto-enabling local auth", async () => {
    vi.stubEnv("VITE_AUTH_MODE", undefined)
    vi.stubEnv("VITE_ENTRA_TENANT_ID", undefined)
    vi.stubEnv("VITE_ENTRA_SPA_CLIENT_ID", undefined)
    vi.stubEnv("VITE_ENTRA_API_SCOPE", undefined)
    vi.stubEnv("DEV", true)
    const { getSession } = await import("./session")
    await expect(getSession()).rejects.toThrow()
  })
})
