import assert from "node:assert/strict"
import AxeBuilder from "@axe-core/playwright"
import { chromium, firefox, webkit, expect } from "@playwright/test"
import { createServer } from "vite"
import { z } from "zod"

const settings = z
  .object({ api: z.url(), token: z.string().min(1) })
  .parse({ api: process.env.WF_QUEUE_API, token: process.env.WF_QUEUE_TOKEN })
const fixtureSchema = z.object({
  runId: z.uuid(),
  blockingRunId: z.uuid(),
  blockingReservationId: z.uuid(),
})
process.env.KARDIA_API_URL = settings.api
process.env.VITE_AUTH_MODE = "entra"
process.env.VITE_ENTRA_TENANT_ID = "ef8a05a0-451c-4479-9c8d-391b6bc7ee1a"
process.env.VITE_ENTRA_SPA_CLIENT_ID = "60d24b19-f65a-4ed4-96cc-9174bfcacabc"
process.env.VITE_ENTRA_API_SCOPE =
  "api://562b0870-4522-43da-8587-41e9b788f309/api.read"
const server = await createServer({ server: { port: 0, strictPort: false } })
async function change(action: "reset" | "advance" | "release") {
  const response = await fetch(
    new URL(`/api/test/reservation-queue/${action}`, settings.api),
    { method: "POST", headers: { Authorization: `Bearer ${settings.token}` } }
  )
  if (!response.ok)
    assert.fail(
      `Queue fixture ${action}: HTTP ${response.status} ${await response.text()}`
    )
  return response
}
try {
  await server.listen()
  const address = server.httpServer?.address()
  assert(
    address && typeof address !== "string",
    "Queue Vite did not bind a port."
  )
  for (const engine of [chromium, firefox, webkit]) {
    const fixture = fixtureSchema.parse(await (await change("reset")).json())
    const browser = await engine.launch()
    try {
      const context = await browser.newContext({
        baseURL: `http://127.0.0.1:${address.port}`,
      })
      const page = await context.newPage()
      await page.goto("/")
      await page.evaluate(async (jwt) => {
        const path = "/scripts/browser-auth.ts"
        const { hydrateBrowserSession }: typeof import("./browser-auth") =
          await import(path)
        await hydrateBrowserSession(jwt)
      }, settings.token)
      const reads: string[] = []
      page.on("request", (request) => {
        if (new URL(request.url()).pathname.endsWith("/reservation-queue"))
          reads.push(request.method())
      })
      await page.goto(`/runs/${fixture.runId}`)
      const banner = page.getByRole("region", { name: "Reservation queue" })
      await expect(banner).toContainText("queue position 2")
      await expect(banner).toContainText(fixture.blockingReservationId)
      const blocker = banner.getByRole("link", { name: fixture.blockingRunId })
      await expect(blocker).toHaveAttribute(
        "href",
        `/runs/${fixture.blockingRunId}`
      )
      await blocker.focus()
      await expect(blocker).toBeFocused()
      for (const width of [1440, 375])
        for (const dark of [false, true]) {
          await page.setViewportSize({
            width,
            height: width === 375 ? 812 : 1000,
          })
          await page.evaluate(
            (enabled) =>
              document.documentElement.classList.toggle("dark", enabled),
            dark
          )
          const audit = await new AxeBuilder({ page })
            .include('[aria-label="Reservation queue"]')
            .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
            .analyze()
          assert.deepEqual(
            audit.violations.filter(
              (issue) =>
                issue.impact === "serious" || issue.impact === "critical"
            ),
            []
          )
          assert.equal(
            await page.evaluate(
              () => document.documentElement.scrollWidth <= innerWidth
            ),
            true,
            "Queued banner caused horizontal overflow."
          )
        }
      await change("advance")
      await expect(banner).toContainText("queue position 1")
      await change("release")
      await expect(banner).toHaveCount(0)
      assert(
        reads.length >= 3,
        "Queue did not refresh after real database changes."
      )
      assert(
        reads.every((method) => method === "GET"),
        "Queue UI issued a mutation."
      )
      console.log(
        `${engine.name()}: actual signed-JWT/PostgreSQL queue position2+blocker -> position1 -> granted without reload; keyboard/mobile/light/dark; banner axe 0 serious/critical`
      )
      await context.close()
    } finally {
      await browser.close()
    }
  }
} finally {
  await server.close()
}
