import { afterEach, expect, it, vi } from "vitest"
import {
  HubConnectionBuilder,
  HttpTransportType,
  LogLevel,
} from "@microsoft/signalr"
import { accessToken } from "@/lib/auth/session"
import { createConnection } from "./connection"

vi.mock("@/lib/auth/session", () => ({ accessToken: vi.fn() }))

afterEach(() => vi.restoreAllMocks())

it("uses one lazy official MessagePack WebSocket and refreshes authentication for each transport attempt", async () => {
  const url = vi.spyOn(HubConnectionBuilder.prototype, "withUrl")
  const protocol = vi.spyOn(HubConnectionBuilder.prototype, "withHubProtocol")
  const logging = vi.spyOn(HubConnectionBuilder.prototype, "configureLogging")
  const reconnect = vi.spyOn(
    HubConnectionBuilder.prototype,
    "withAutomaticReconnect"
  )
  vi.mocked(accessToken)
    .mockResolvedValueOnce("first")
    .mockResolvedValueOnce("renewed")
    .mockResolvedValueOnce(null)
  const connection = await createConnection()
  const [path, options] = url.mock.calls[0] ?? []
  expect(path).toBe(new URL("/hubs/live/v1", location.origin).href)
  if (!options || typeof options !== "object" || !options.accessTokenFactory) {
    throw new Error("The live connection has no token factory.")
  }
  expect(options.transport).toBe(HttpTransportType.WebSockets)
  expect(options.skipNegotiation).toBe(true)
  expect(await options.accessTokenFactory()).toBe("first")
  expect(await options.accessTokenFactory()).toBe("renewed")
  expect(await options.accessTokenFactory()).toBe("")
  expect(protocol.mock.calls[0]?.[0].name).toBe("messagepack")
  expect(logging).toHaveBeenCalledWith(LogLevel.None)
  expect(reconnect).toHaveBeenCalledOnce()
  await connection.stop()
})
