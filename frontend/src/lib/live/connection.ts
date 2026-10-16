import { accessToken } from "@/lib/auth/session"

export interface LiveTransport {
  start(): Promise<void>
  stop(): Promise<void>
  invoke(method: string, scope: string, id: string): Promise<unknown>
  on(method: string, callback: (message: unknown) => void): void
  onclose(callback: (error?: Error) => void): void
  onreconnecting(callback: (error?: Error) => void): void
  onreconnected(callback: () => void): void
}

export async function createConnection(
  getAccessToken: () => Promise<string | null> = accessToken
): Promise<LiveTransport> {
  const [
    { HubConnectionBuilder, HttpTransportType, LogLevel },
    { MessagePackHubProtocol },
  ] = await Promise.all([
    import("@microsoft/signalr"),
    import("@microsoft/signalr-protocol-msgpack"),
  ])
  return new HubConnectionBuilder()
    .withUrl(new URL("/hubs/live/v1", location.origin).href, {
      transport: HttpTransportType.WebSockets,
      skipNegotiation: true,
      accessTokenFactory: async () => (await getAccessToken()) ?? "",
    })
    .withHubProtocol(new MessagePackHubProtocol())
    .withAutomaticReconnect()
    .configureLogging(LogLevel.None)
    .build()
}
