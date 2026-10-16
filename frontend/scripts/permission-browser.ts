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
  })
  .parse({
    api: process.env.WF_PERMISSION_API,
    token: process.env.WF_PERMISSION_TOKEN,
    viewer: process.env.WF_PERMISSION_VIEWER_WORKFLOW,
    operator: process.env.WF_PERMISSION_OPERATOR_WORKFLOW,
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
      console.log(
        `${engine.name()}: signed JWT Operator -> Viewer -> denied scope; draft retained; no mutation; axe clean`
      )
    } finally {
      await browser.close()
    }
  }
} finally {
  await server.close()
}
