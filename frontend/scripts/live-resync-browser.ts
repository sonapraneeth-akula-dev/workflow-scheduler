import assert from "node:assert/strict"
import { rm } from "node:fs/promises"
import { resolve } from "node:path"
import { chromium, firefox, webkit } from "@playwright/test"
import { createServer } from "vite"
import { z } from "zod"
import type { RunDelta } from "../src/lib/live/messages"
import type { RunSnapshot } from "../src/lib/live/runApi"
import type { RunStore } from "../src/lib/live/runStore"

type Change = RunDelta["events"][number]
interface Probe {
  store: RunStore
  applied: Change[]
  statuses: string[]
  initial: RunSnapshot
  reconnectHints: string[]
  finish(): Promise<void>
}
declare global {
  interface Window {
    resyncProbe: Probe
  }
}

const settings = z
  .object({
    api: z.url(),
    control: z.url(),
    token: z.string().min(1),
    run: z.uuid(),
  })
  .parse({
    api: process.env.WF_RESYNC_API,
    control: process.env.WF_RESYNC_CONTROL,
    token: process.env.WF_RESYNC_TOKEN,
    run: process.env.WF_RESYNC_RUN,
  })
const appendSchema = z.object({ lastSeq: z.int(), apiRunning: z.boolean() })
const faultSchema = z.object({
  aborted: z.int().positive(),
  generation: z.int().positive(),
  portClosed: z.boolean().optional(),
})
const eventSchema = z.object({
  seq: z.int().positive(),
  at: z.string(),
  entity: z.int(),
  key: z.string().nullable(),
  state: z.int(),
  abort: z.null(),
})
const tailSchema = z.object({
  runId: z.uuid(),
  lastSeq: z.int(),
  events: z.array(eventSchema),
})
const auditSchema = z.object({
  events: z.array(eventSchema),
  generation: z.int().positive(),
  apiRunning: z.boolean(),
})
async function control(path: string, method = "POST"): Promise<unknown> {
  const response = await fetch(new URL(`/fixture/${path}`, settings.control), {
    method,
    headers: { Authorization: `Bearer ${settings.token}` },
  })
  assert.equal(response.status, 200, `Fixture ${path}: HTTP ${response.status}`)
  return response.json()
}
async function append(count: number, delay = 0, pauseAfter = 0) {
  return appendSchema.parse(
    await control(`append/${count}?delay=${delay}&pauseAfter=${pauseAfter}`)
  )
}

process.env.KARDIA_API_URL = settings.api
process.env.VITE_AUTH_MODE = "entra"
process.env.VITE_ENTRA_TENANT_ID = "ef8a05a0-451c-4479-9c8d-391b6bc7ee1a"
process.env.VITE_ENTRA_SPA_CLIENT_ID = "60d24b19-f65a-4ed4-96cc-9174bfcacabc"
process.env.VITE_ENTRA_API_SCOPE =
  "api://562b0870-4522-43da-8587-41e9b788f309/api.read"
const cacheDir = resolve("node_modules", `.vite-live-resync-${process.pid}`)
const server = await createServer({
  cacheDir,
  optimizeDeps: {
    include: [
      "@azure/msal-browser",
      "@microsoft/signalr",
      "@microsoft/signalr-protocol-msgpack",
      "openapi-fetch",
      "zod",
    ],
  },
  server: { port: 0, strictPort: false },
  plugins: [
    {
      name: "physical-resync-probe",
      configureServer(vite) {
        vite.middlewares.use("/live-resync-probe", (_request, response) => {
          response.setHeader("Content-Type", "text/html")
          response.end(
            "<!doctype html><title>Physical live resync probe</title>"
          )
        })
      },
    },
  ],
})
try {
  await server.listen()
  const address = server.httpServer?.address()
  assert(address && typeof address !== "string", "Vite did not bind a port.")
  for (const engine of [chromium, firefox, webkit]) {
    const browser = await engine.launch()
    try {
      const context = await browser.newContext({
        baseURL: `http://127.0.0.1:${address.port}`,
      })
      const page = await context.newPage()
      const reads: string[] = []
      const tails: { after: number; page: z.infer<typeof tailSchema> }[] = []
      const responses: Promise<void>[] = []
      const sockets: { closed: boolean; binary: boolean }[] = []
      page.on("request", (request) => {
        const url = new URL(request.url())
        if (url.pathname.startsWith(`/api/v1/runs/${settings.run}`)) {
          reads.push(url.pathname + url.search)
        }
      })
      page.on("response", (response) => {
        const url = new URL(response.url())
        if (url.pathname === `/api/v1/runs/${settings.run}/events`) {
          responses.push(
            (async () => {
              assert.equal(response.status(), 200)
              tails.push({
                after: Number(url.searchParams.get("afterSeq")),
                page: tailSchema.parse(await response.json()),
              })
            })()
          )
        }
      })
      page.on("websocket", (socket) => {
        if (new URL(socket.url()).pathname !== "/hubs/live/v1") return
        const observed = { closed: false, binary: false }
        sockets.push(observed)
        socket.on("close", () => (observed.closed = true))
        socket.on("framereceived", ({ payload }) => {
          if (typeof payload !== "string") observed.binary = true
        })
      })
      await page.goto("/live-resync-probe")
      const initial = await page.evaluate(
        async ({ run, token }) => {
          const msalModule = "/node_modules/@azure/msal-browser/dist/index.mjs"
          const {
            PublicClientApplication,
          }: typeof import("@azure/msal-browser") = await import(msalModule)
          const claims: unknown = JSON.parse(atob(token.split(".")[1]!))
          if (
            !claims ||
            typeof claims !== "object" ||
            !("oid" in claims) ||
            typeof claims.oid !== "string" ||
            !("tid" in claims) ||
            typeof claims.tid !== "string"
          ) {
            throw new Error("Fixture JWT is missing its real signed identity.")
          }
          const authority = `https://login.microsoftonline.com/${claims.tid}`
          const application = new PublicClientApplication({
            auth: {
              clientId: "60d24b19-f65a-4ed4-96cc-9174bfcacabc",
              authority,
              redirectUri: location.origin,
            },
            cache: { cacheLocation: "sessionStorage" },
          })
          await application.initialize()
          const scopes = ["api://562b0870-4522-43da-8587-41e9b788f309/api.read"]
          // Seed a real signed JWT through MSAL's public cache hydration API.
          await application.hydrateCache(
            {
              authority,
              uniqueId: claims.oid,
              tenantId: claims.tid,
              scopes,
              account: {
                homeAccountId: `${claims.oid}.${claims.tid}`,
                localAccountId: claims.oid,
                tenantId: claims.tid,
                environment: "login.microsoftonline.com",
                username: "resync@example.test",
              },
              idToken: token,
              idTokenClaims: claims,
              accessToken: token,
              fromCache: false,
              expiresOn: new Date(Date.now() + 600000),
              tokenType: "Bearer",
              correlationId: crypto.randomUUID(),
            },
            { scopes }
          )
          const sessionModule = "/src/lib/auth/session.ts"
          const {
            getSession,
            accessToken,
          }: typeof import("../src/lib/auth/session") = await import(
            sessionModule
          )
          const session = await getSession()
          if (
            !session ||
            session.isDevelopment ||
            (await accessToken()) !== token
          ) {
            throw new Error(
              "Production authentication did not acquire the signed fixture JWT."
            )
          }
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
          const messagesModule = "/src/lib/live/messages.ts"
          const { parseReconnect }: typeof import("../src/lib/live/messages") =
            await import(messagesModule)
          const reconnectHints: string[] = []
          const manager = new LiveSubscriptions(async () => {
            const connection = await createConnection()
            connection.on("Reconnect", (value) => {
              reconnectHints.push(parseReconnect(value).reason)
            })
            return connection
          })
          const store = new RunStore(run, manager)
          const applied: Change[] = []
          const statuses: string[] = []
          // Observe actual per-event production application, not coalesced renders.
          const observed = store as unknown as { apply(event: Change): void }
          const apply = observed.apply.bind(store)
          observed.apply = (event) => {
            const before = store.getSnapshot().run
            if (!before || event.seq !== before.lastSeq + 1) {
              throw new Error(
                "Production apply missed, duplicated or reordered an event."
              )
            }
            apply(event)
            const after = store.getSnapshot().run
            const state =
              event.entity === 0
                ? after?.status
                : event.entity === 1
                  ? after?.nodes[event.key!]
                  : after?.tasks[event.key!]
            if (after?.lastSeq !== event.seq || state !== event.state) {
              throw new Error(
                "Production apply did not update the exact graph entity."
              )
            }
            applied.push(event)
          }
          const release = store.subscribe(() => {
            const snapshot = store.getSnapshot()
            statuses.push(snapshot.status)
          })
          const deadline = performance.now() + 20000
          while (store.getSnapshot().status !== "live") {
            if (store.getSnapshot().error) throw store.getSnapshot().error
            if (performance.now() > deadline)
              throw new Error("Initial snapshot timed out.")
            await new Promise((resolve) => setTimeout(resolve, 10))
          }
          const initial = store.getSnapshot().run!
          window.resyncProbe = {
            store,
            initial,
            reconnectHints,
            applied,
            statuses,
            finish: async () => {
              release()
              await manager.reset()
            },
          }
          return initial.lastSeq
        },
        { run: settings.run, token: settings.token }
      )
      const caughtUp = async (sequence: number) => {
        await page.waitForFunction(
          (expected) => {
            const state = window.resyncProbe.store.getSnapshot()
            if (state.error) throw state.error
            return state.status === "live" && state.run?.lastSeq === expected
          },
          sequence,
          { timeout: 30000 }
        )
      }
      const live = await append(8)
      await caughtUp(live.lastSeq)
      const evidence: string[] = []
      const faults =
        process.env.WF_RESYNC_DRAIN === "true"
          ? (["drain"] as const)
          : (["drop", "stop"] as const)
      for (const fault of faults) {
        const before = await page.evaluate(
          () => window.resyncProbe.store.getSnapshot().run!.lastSeq
        )
        const readCount = reads.length
        const tailCount = tails.length
        const currentSocket = sockets.at(-1)!
        assert(
          currentSocket.binary,
          "The real hub did not deliver MessagePack."
        )
        const lost = faultSchema.parse(await control(fault))
        if (fault !== "drop") assert.equal(lost.portClosed, true)
        if (fault === "drain")
          assert.deepEqual(
            await page.evaluate(() => window.resyncProbe.reconnectHints),
            ["server_draining"],
            "Graceful drain did not deliver its real MessagePack reconnect hint."
          )
        await page.waitForFunction(
          () => window.resyncProbe.store.getSnapshot().status === "reconnecting"
        )
        assert(
          currentSocket.closed,
          "Fault did not physically close the WebSocket."
        )
        const offline = await append(12, 10)
        assert.equal(offline.lastSeq, before + 12)
        assert.equal(offline.apiRunning, fault === "drop")
        const offlineAudit = auditSchema.parse(await control("audit", "GET"))
        assert.equal(offlineAudit.events.at(-1)?.seq, offline.lastSeq)
        assert.equal(
          await page.evaluate(
            () => window.resyncProbe.store.getSnapshot().run!.lastSeq
          ),
          before,
          "The disconnected client applied an offline write."
        )
        const continuing = append(48, 10, 24)
        const restored = z
          .object({ generation: z.int(), api: z.string().optional() })
          .parse(await control(fault === "drop" ? "resume" : "start"))
        assert.equal(
          restored.generation,
          lost.generation + (fault !== "drop" ? 1 : 0)
        )
        if (fault !== "drop") assert.equal(restored.api, settings.api)
        await caughtUp(before + 36)
        await control("continue")
        const recovered = await continuing
        assert.equal(recovered.lastSeq, before + 60)
        await caughtUp(recovered.lastSeq)
        await Promise.all(responses)
        assert(
          tails
            .slice(tailCount)
            .some((tail) => tail.page.lastSeq < recovered.lastSeq),
          "The independent writer did not continue committing after the recovery tail."
        )
        const recoveryReads = reads.slice(readCount)
        assert(
          recoveryReads.includes(
            `/api/v1/runs/${settings.run}/events?afterSeq=${before}`
          ),
          "Reconnect did not resume from the last applied committed sequence."
        )
        assert(
          recoveryReads.every((url) => url.includes("/events?afterSeq=")),
          "Reconnect replaced ordered recovery with a snapshot reload."
        )
        const stableReads = reads.length
        await page.waitForTimeout(700)
        assert.equal(
          reads.length,
          stableReads,
          "Recovery used periodic REST polling."
        )
        const pushed = await append(4)
        await caughtUp(pushed.lastSeq)
        assert.equal(
          reads.length,
          stableReads,
          "Post-reconnect push required a REST read."
        )
        evidence.push(
          `${fault}: ${before}->${offline.lastSeq} offline ->${pushed.lastSeq}, ${recoveryReads.length} ordered tail(s), generation ${lost.generation}->${restored.generation}`
        )
      }
      const durable = auditSchema.parse(await control("audit", "GET"))
      for (const tail of tails) {
        assert.equal(tail.page.runId, settings.run)
        assert.deepEqual(
          tail.page.events,
          durable.events.filter(
            (event) => event.seq > tail.after && event.seq <= tail.page.lastSeq
          ),
          "Actual HTTP recovery tail missed or reordered committed PostgreSQL rows."
        )
      }
      const result = await page.evaluate(async (run) => {
        const probe = window.resyncProbe
        const sessionModule = "/src/lib/auth/session.ts"
        const { accessToken }: typeof import("../src/lib/auth/session") =
          await import(sessionModule)
        const response = await fetch(`/api/v1/runs/${run}`, {
          headers: { Authorization: `Bearer ${await accessToken()}` },
        })
        if (!response.ok) throw new Error("Authoritative graph request failed.")
        const authoritative: unknown = await response.json()
        return {
          initial: probe.initial,
          applied: probe.applied,
          statuses: probe.statuses,
          recovered: probe.store.getSnapshot().run,
          authoritative,
        }
      }, settings.run)
      assert.deepEqual(
        result.applied,
        durable.events.filter((event) => event.seq > initial),
        "Recovered apply audit differs from the exact committed PostgreSQL event tail."
      )
      assert.equal(result.applied.length, 8 + faults.length * 64)
      assert.deepEqual(result.recovered, result.authoritative)
      assert.equal(
        reads.filter((url) => !url.includes("/events?")).length,
        2,
        "Expected only initial and final authoritative graph reads."
      )
      assert(
        sockets.filter((socket) => socket.binary).length >= faults.length + 1,
        "Real MessagePack sockets were not reestablished after every fault."
      )
      assert(result.statuses.includes("reconnecting"))
      console.log(
        `${engine.name()}: ${evidence.join("; ")}; ${result.applied.length} exact contiguous durable applies ${initial + 1}..${durable.events.at(-1)?.seq}; exact graph, no polling`
      )
      await page.evaluate(() => window.resyncProbe.finish())
    } finally {
      await browser.close()
    }
  }
} finally {
  await server.close()
  await rm(cacheDir, { recursive: true, force: true })
}
