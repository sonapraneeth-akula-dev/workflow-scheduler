import { afterEach, describe, expect, it, vi } from "vitest"

afterEach(() => {
  vi.unstubAllEnvs()
  vi.unstubAllGlobals()
  vi.resetModules()
})

describe("authentication cannot silently bypass Entra", () => {
  it("allows the explicit HA lab build only on IPv4 loopback", async () => {
    vi.stubEnv("VITE_AUTH_MODE", "ha-lab")
    vi.stubEnv("MODE", "ha-lab")
    vi.stubEnv("DEV", false)
    vi.stubGlobal("location", { hostname: "127.0.0.1" })
    const { getSession, accessToken } = await import("./session")
    await expect(getSession()).resolves.toEqual({
      displayName: "Local developer",
      isDevelopment: true,
    })
    await expect(accessToken()).resolves.toBeNull()
  })

  it.each([
    ["production", "127.0.0.1"],
    ["development", "127.0.0.1"],
    ["local-compose", "127.0.0.1"],
    ["ha-lab", "localhost"],
    ["ha-lab", "[::1]"],
    ["ha-lab", "127.2.6.2"],
    ["ha-lab", "dev.kardia.sonapraneeth.in"],
    ["ha-lab", "192.168.247.11"],
    ["ha-lab", "127.0.0.1.attacker.invalid"],
  ])("rejects an HA lab bypass in mode %s on %s", async (mode, hostname) => {
    vi.stubEnv("VITE_AUTH_MODE", "ha-lab")
    vi.stubEnv("MODE", mode)
    vi.stubEnv("DEV", false)
    vi.stubGlobal("location", { hostname })
    const { getSession, accessToken } = await import("./session")
    await expect(getSession()).rejects.toThrow("explicit build mode")
    await expect(accessToken()).rejects.toThrow("explicit build mode")
  })

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

  it.each([
    "127.2.6.2",
    "127.2.6.7",
    "dev.kardia.sonapraneeth.in",
    "ppe.kardia.sonapraneeth.in",
  ])("allows the explicit local-compose build only on %s", async (hostname) => {
    vi.stubEnv("VITE_AUTH_MODE", "local-compose")
    vi.stubEnv("MODE", "local-compose")
    vi.stubEnv("DEV", false)
    vi.stubGlobal("location", { hostname })
    const { getSession, accessToken } = await import("./session")
    await expect(getSession()).resolves.toEqual({
      displayName: "Local developer",
      isDevelopment: true,
    })
    await expect(accessToken()).resolves.toBeNull()
  })

  it.each([
    ["production", "127.2.6.7"],
    ["development", "127.2.6.2"],
    ["local-compose", "localhost"],
    ["local-compose", "127.0.0.1"],
    ["local-compose", "example.invalid"],
    ["local-compose", "ppe.kardia.sonapraneeth.in.attacker.invalid"],
  ])(
    "rejects a local-compose bypass in mode %s on %s",
    async (mode, hostname) => {
      vi.stubEnv("VITE_AUTH_MODE", "local-compose")
      vi.stubEnv("MODE", mode)
      vi.stubEnv("DEV", false)
      vi.stubGlobal("location", { hostname })
      const { getSession, accessToken } = await import("./session")
      await expect(getSession()).rejects.toThrow("explicit build mode")
      await expect(accessToken()).rejects.toThrow("explicit build mode")
    }
  )
})
