import assert from "node:assert/strict"
import { chromium, firefox, webkit, expect } from "@playwright/test"
import { createServer } from "vite"
import { z } from "zod"

const settings = z
  .object({
    api: z.url(),
    token: z.string().min(1),
    run: z.uuid(),
    type: z.string().min(1),
    denied: z.string().min(1),
  })
  .parse({
    api: process.env.WF_LIVE_API,
    token: process.env.WF_LIVE_TOKEN,
    run: process.env.WF_LIVE_RUN,
    type: process.env.WF_LIVE_TYPE,
    denied: process.env.WF_LIVE_DENIED,
  })
process.env.KARDIA_API_URL = settings.api
process.env.VITE_AUTH_MODE = "development"
const server = await createServer({ server: { port: 0, strictPort: false } })
try {
  await server.listen()
  const address = server.httpServer?.address()
  assert(
    address && typeof address !== "string",
    "Vite did not bind a test port."
  )
  for (const engine of [chromium, firefox, webkit]) {
    const browser = await engine.launch()
    try {
      const context = await browser.newContext({
        baseURL: `http://127.0.0.1:${address.port}`,
        extraHTTPHeaders: { Authorization: `Bearer ${settings.token}` },
      })
      const page = await context.newPage()
      let connections = 0
      let closed = 0
      let binaryFrames = 0
      let messagePackHandshake = false
      page.on("websocket", (socket) => {
        if (new URL(socket.url()).pathname !== "/hubs/live/v1") return
        connections++
        socket.on("framesent", (frame) => {
          if (
            typeof frame.payload === "string" &&
            frame.payload.includes('"protocol":"messagepack"')
          ) {
            messagePackHandshake = true
          }
        })
        socket.on("framereceived", (frame) => {
          if (Buffer.isBuffer(frame.payload)) binaryFrames++
        })
        socket.on("close", () => closed++)
      })
      await page.goto("/")
      await page.waitForLoadState("networkidle")
      const reads: string[] = []
      page.on("request", (request) => {
        if (new URL(request.url()).pathname.startsWith("/api/"))
          reads.push(request.method())
      })
      const result = await page.evaluate(
        async ({ run, type, denied, token }) => {
          const modulePath = "/src/lib/live/subscriptions.ts"
          const {
            LiveSubscriptions,
          }: typeof import("../src/lib/live/subscriptions") = await import(
            modulePath
          )
          const connectionPath = "/src/lib/live/connection.ts"
          const {
            createConnection,
          }: typeof import("../src/lib/live/connection") = await import(
            connectionPath
          )
          const liveSubscriptions = new LiveSubscriptions(() =>
            createConnection(async () => token)
          )
          const first = liveSubscriptions.retain({ scope: "run", id: run })
          const second = liveSubscriptions.retain({
            scope: "run",
            id: run.toUpperCase(),
          })
          const list = liveSubscriptions.retain({ scope: "type", id: type })
          const forbidden = liveSubscriptions.retain({
            scope: "type",
            id: denied,
          })
          const wait = async (ready: () => boolean) => {
            const deadline = performance.now() + 15000
            while (!ready()) {
              for (const store of [first, second, list]) {
                const error = store.getSnapshot().error
                if (error) {
                  const detail =
                    error.cause instanceof Error
                      ? error.cause.message.replace(
                          /access_token=[^&\s]*/gi,
                          "access_token=[redacted]"
                        )
                      : ""
                  throw new Error(`${error.message} ${detail}`)
                }
              }
              if (performance.now() > deadline)
                throw new Error(
                  "Actual live browser scope did not become ready."
                )
              await new Promise((resolve) => setTimeout(resolve, 10))
            }
          }
          let notifications = 0
          const sequences: number[] = []
          const unsubscribe = first.subscribe(() => {
            notifications++
            const delta = first.getSnapshot().delta
            if (delta && "events" in delta)
              sequences.push(...delta.events.map((change) => change.seq))
          })
          try {
            await wait(
              () =>
                first.getSnapshot().status === "live" &&
                list.getSnapshot().status === "live" &&
                forbidden.getSnapshot().status === "error" &&
                sequences.length >= 64 &&
                list.getSnapshot().delta !== null
            )
            assertShared()
            if (!forbidden.getSnapshot().error?.message.includes("HTTP 403")) {
              throw new Error(
                "The real live server did not reject the ungranted type."
              )
            }
            const liveList = list.getSnapshot().delta
            if (
              !liveList ||
              !("items" in liveList) ||
              !liveList.items.some((item) => item.runId === run)
            ) {
              throw new Error(
                "The real type subscription received no matching run summary."
              )
            }
            const before = notifications
            const last = sequences.at(-1)
            first.release()
            forbidden.release()
            await wait(() => {
              const delta = second.getSnapshot().delta
              return (
                !!delta &&
                "events" in delta &&
                delta.events.some(
                  (change) => last !== undefined && change.seq > last
                )
              )
            })
            if (notifications !== before)
              throw new Error(
                "A released view still received live notifications."
              )
            for (let index = 1; index < sequences.length; index++) {
              const previous = sequences[index - 1]
              const current = sequences[index]
              if (previous === undefined || current !== previous + 1) {
                throw new Error(
                  "Actual MessagePack browser events were duplicated, lost or reordered."
                )
              }
            }
            return { events: sequences.length, notifications }
          } finally {
            unsubscribe()
            first.release()
            second.release()
            list.release()
            forbidden.release()
            await liveSubscriptions.reset()
          }
          function assertShared() {
            if (first.getSnapshot() !== second.getSnapshot())
              throw new Error("Views did not share the same scope store.")
          }
        },
        {
          run: settings.run,
          type: settings.type,
          denied: settings.denied,
          token: settings.token,
        }
      )
      await expect.poll(() => closed).toBe(1)
      assert.equal(
        connections,
        1,
        "Multiple views opened more than one live connection."
      )
      assert(messagePackHandshake, "The browser did not negotiate MessagePack.")
      assert(
        binaryFrames > 0,
        "The actual server sent no binary MessagePack frames."
      )
      assert(result.events >= 64)
      assert(result.notifications < result.events)
      assert.deepEqual(
        reads,
        [],
        "Live subscriptions issued polling REST requests."
      )
      console.log(
        `${engine.name()}: one actual JWT MessagePack WebSocket; ${result.events} ordered events; shared scoped stores, fresh 403, frame batching and final cleanup`
      )
    } finally {
      await browser.close()
    }
  }
} finally {
  await server.close()
}
