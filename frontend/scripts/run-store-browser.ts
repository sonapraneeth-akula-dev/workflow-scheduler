import assert from "node:assert/strict"
import { chromium, firefox, webkit } from "@playwright/test"
import { createServer } from "vite"
import { z } from "zod"

const settings = z
  .object({
    api: z.url(),
    token: z.string().min(1),
    run: z.uuid(),
  })
  .parse({
    api: process.env.WF_LIVE_API,
    token: process.env.WF_LIVE_TOKEN,
    run: process.env.WF_LIVE_RUN,
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
      const reads: string[] = []
      let conflicts = 0
      let sockets = 0
      page.on("request", (request) => {
        const url = new URL(request.url())
        if (url.pathname.startsWith(`/api/v1/runs/${settings.run}`))
          reads.push(url.pathname + url.search)
      })
      page.on("response", (response) => {
        if (
          response.url().includes(`/api/v1/runs/${settings.run}/events?`) &&
          response.status() === 409
        )
          conflicts++
      })
      page.on("websocket", (socket) => {
        if (new URL(socket.url()).pathname === "/hubs/live/v1") sockets++
      })
      await page.goto("/")
      const result = await page.evaluate(
        async ({ run, token }) => {
          const storeModule = "/src/lib/live/runStore.ts"
          const subscriptionsModule = "/src/lib/live/subscriptions.ts"
          const connectionModule = "/src/lib/live/connection.ts"
          const { RunStore }: typeof import("../src/lib/live/runStore") =
            await import(storeModule)
          const {
            LiveSubscriptions,
          }: typeof import("../src/lib/live/subscriptions") = await import(
            subscriptionsModule
          )
          const {
            createConnection,
          }: typeof import("../src/lib/live/connection") = await import(
            connectionModule
          )
          let blocked = false
          let dropped = 0
          const manager = new LiveSubscriptions(async () => {
            const transport = await createConnection(async () => token)
            const on = transport.on.bind(transport)
            transport.on = (method, receive) =>
              on(method, (message) => {
                if (method === "RunDelta" && blocked) {
                  dropped++
                  return
                }
                receive(message)
              })
            return transport
          })
          const store = new RunStore(run, manager)
          const states: number[] = []
          const release = store.subscribe(() => {
            const sequence = store.getSnapshot().run?.lastSeq
            if (sequence !== undefined) states.push(sequence)
          })
          const wait = async (predicate: () => boolean) => {
            const deadline = performance.now() + 20000
            while (!predicate()) {
              const error = store.getSnapshot().error
              if (error) throw error
              if (performance.now() > deadline)
                throw new Error("Actual run store recovery timed out.")
              await new Promise((resolve) => setTimeout(resolve, 10))
            }
          }
          const append = async (count: number) => {
            const response = await fetch(
              `/api/test/run-store/append/${count}`,
              {
                method: "POST",
              }
            )
            if (!response.ok)
              throw new Error(`Actual event append failed: ${response.status}`)
            const value: unknown = await response.json()
            if (
              !value ||
              typeof value !== "object" ||
              !("lastSeq" in value) ||
              typeof value.lastSeq !== "number"
            )
              throw new Error(
                "Actual append did not return its durable sequence."
              )
            return value.lastSeq
          }
          try {
            await wait(() => store.getSnapshot().status === "live")
            const initial = store.getSnapshot().run!.lastSeq
            blocked = true
            await append(2)
            await wait(() => dropped > 0)
            blocked = false
            const gapEnd = await append(2)
            await wait(
              () =>
                store.getSnapshot().status === "live" &&
                store.getSnapshot().run?.lastSeq === gapEnd
            )
            if (gapEnd !== initial + 4)
              throw new Error(
                "The actual gap fixture did not append four events."
              )
            blocked = true
            const end = await append(6001)
            const resync = await fetch("/api/test/run-store/resync", {
              method: "POST",
            })
            if (resync.status !== 204)
              throw new Error("The server did not emit an actual Resync frame.")
            await wait(
              () =>
                store.getSnapshot().status === "live" &&
                store.getSnapshot().run?.lastSeq === end
            )
            blocked = false
            const authoritative = await fetch(`/api/v1/runs/${run}`)
            if (!authoritative.ok)
              throw new Error("The final authoritative snapshot failed.")
            const current: unknown = await authoritative.json()
            if (
              JSON.stringify(current) !==
              JSON.stringify(store.getSnapshot().run)
            )
              throw new Error(
                "Recovered client state differs from the actual authoritative graph."
              )
            for (let index = 1; index < states.length; index++) {
              const previous = states[index - 1]
              const next = states[index]
              if (
                previous === undefined ||
                next === undefined ||
                next < previous
              )
                throw new Error("The recovered graph sequence moved backwards.")
            }
            await new Promise((resolve) => setTimeout(resolve, 600))
            return { initial, gapEnd, end, dropped }
          } finally {
            release()
            await manager.reset()
          }
        },
        { run: settings.run, token: settings.token }
      )
      assert.equal(
        sockets,
        1,
        "The run store opened more than one MessagePack connection."
      )
      assert.equal(
        conflicts,
        1,
        "The >5000-event gap did not produce one actual HTTP 409."
      )
      assert.equal(
        reads.filter((url) => url.includes("/events?")).length,
        2,
        "Unexpected gap resync requests."
      )
      assert.equal(
        reads.filter((url) => !url.includes("/events?")).length,
        3,
        "Expected initial snapshot, 409 reload and final authoritative comparison."
      )
      assert(
        reads.some((url) => url.endsWith(`/events?afterSeq=${result.initial}`))
      )
      assert(
        reads.some((url) => url.endsWith(`/events?afterSeq=${result.gapEnd}`))
      )
      assert(result.dropped > 0)
      console.log(
        `${engine.name()}: actual JWT/MessagePack dropped-frame gap ${result.initial}->${result.gapEnd}; HTTP409 snapshot reload to ${result.end}; exact graph, ordered state, no REST polling`
      )
    } finally {
      await browser.close()
    }
  }
} finally {
  await server.close()
}
