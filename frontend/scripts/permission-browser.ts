import assert from "node:assert/strict"
import AxeBuilder from "@axe-core/playwright"
import { chromium, firefox, webkit, expect } from "@playwright/test"
import { createServer } from "vite"
import { z } from "zod"

const settings = z
  .object({
    api: z.url(),
    token: z.string().min(1),
    viewer: z.string().min(1),
    operator: z.string().min(1),
    run: z.uuid(),
  })
  .parse({
    api: process.env.WF_PERMISSION_API,
    token: process.env.WF_PERMISSION_TOKEN,
    viewer: process.env.WF_PERMISSION_VIEWER_WORKFLOW,
    operator: process.env.WF_PERMISSION_OPERATOR_WORKFLOW,
    run: process.env.WF_PERMISSION_VIEWER_RUN,
  })
process.env.KARDIA_API_URL = settings.api
process.env.VITE_AUTH_MODE = "entra"
process.env.VITE_ENTRA_TENANT_ID = "ef8a05a0-451c-4479-9c8d-391b6bc7ee1a"
process.env.VITE_ENTRA_SPA_CLIENT_ID = "60d24b19-f65a-4ed4-96cc-9174bfcacabc"
process.env.VITE_ENTRA_API_SCOPE =
  "api://562b0870-4522-43da-8587-41e9b788f309/api.read"
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
      })
      const page = await context.newPage()
      await page.goto("/")
      await page.evaluate(async (jwt) => {
        const path = "/scripts/browser-auth.ts"
        const { hydrateBrowserSession }: typeof import("./browser-auth") =
          await import(path)
        await hydrateBrowserSession(jwt)
      }, settings.token)
      await page.reload()
      await expect(page.getByRole("button", { name: "Sign out" })).toBeVisible()
      const mutations: string[] = []
      page.on("request", (request) => {
        if (request.url().includes("/api/") && request.method() !== "GET") {
          mutations.push(
            request.method() + " " + new URL(request.url()).pathname
          )
        }
      })
      await page.goto(
        "/schedules/new?workflow=" + encodeURIComponent(settings.operator)
      )
      await expect(
        page.getByRole("button", { name: "Create schedule", exact: true })
      ).toBeDisabled()
      await page
        .getByLabel("Variable overrides (JSON object)")
        .fill('{"draft":"preserved"}')
      await page
        .getByLabel("Workflow identity", { exact: true })
        .fill(settings.viewer)
      await expect(
        page.getByText(/Read-only: Operator permission/)
      ).toBeVisible()
      await expect(
        page.getByRole("button", { name: "Create schedule", exact: true })
      ).toHaveCount(0)
      await expect(
        page.getByRole("button", {
          name: /^(Trigger|Abort|Pause schedule|Delete schedule)$/,
        })
      ).toHaveCount(0)
      await expect(
        page.getByLabel("Variable overrides (JSON object)")
      ).toHaveValue('{"draft":"preserved"}')
      await page
        .getByRole("form", { name: "Schedule", exact: true })
        .evaluate((form: HTMLFormElement) => form.requestSubmit())
      await expect(
        page
          .getByRole("alert")
          .filter({ hasText: "Operator permission is required" })
      ).toContainText("Operator permission is required")
      await page.getByRole("button", { name: "Dark theme" }).click()
      await page.setViewportSize({ width: 375, height: 812 })
      const accessibility = await new AxeBuilder({ page })
        .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
        .analyze()
      assert.deepEqual(
        accessibility.violations.filter(
          (issue) => issue.impact === "critical" || issue.impact === "serious"
        ),
        []
      )
      await page
        .getByLabel("Workflow identity", { exact: true })
        .fill("ungranted/release/workflow")
      await expect(
        page
          .getByRole("alert")
          .filter({ hasText: "The caller does not have the required role" })
      ).toBeVisible()
      await expect(
        page.getByRole("button", { name: "Create schedule", exact: true })
      ).toHaveCount(0)
      assert.deepEqual(
        mutations,
        [],
        "Viewer or pending permissions issued a mutation or preview."
      )
      const permissionRead = page.waitForResponse(
        (response) =>
          new URL(response.url()).pathname === "/api/v1/permissions/run" &&
          new URL(response.url()).searchParams.get("id") === settings.run
      )
      await page.goto(`/runs/${settings.run}`)
      const permission = await permissionRead
      assert.equal(permission.status(), 200)
      const viewer = z
        .object({ viewer: z.boolean(), operator: z.boolean() })
        .parse(await permission.json())
      assert.deepEqual(viewer, { viewer: true, operator: false })
      await expect(
        page.getByRole("region", { name: "Run controls" })
      ).toContainText("Queued")
      await expect(
        page.getByRole("button", { name: "Abort run", exact: true })
      ).toHaveCount(0)
      assert.deepEqual(
        mutations,
        [],
        "Actual Viewer run page issued a mutation."
      )
      const denied = await context.request.post(
        `/api/v1/runs/${settings.run}/abort`,
        {
          headers: { Authorization: `Bearer ${settings.token}` },
          data: { reason: "Viewer must not abort", source: "UI" },
        }
      )
      assert.equal(
        denied.status(),
        403,
        "Actual abort endpoint must reject Viewer."
      )
      const runAccessibility = await new AxeBuilder({ page })
        .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
        .analyze()
      assert.deepEqual(
        runAccessibility.violations.filter(
          (issue) => issue.impact === "critical" || issue.impact === "serious"
        ),
        []
      )
      console.log(
        `${engine.name()}: signed JWT Operator -> Viewer -> denied scope; draft retained; real Viewer run has no Abort, direct abort403; no UI mutation; axe clean`
      )
    } finally {
      await browser.close()
    }
  }
} finally {
  await server.close()
}
