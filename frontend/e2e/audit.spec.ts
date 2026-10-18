import AxeBuilder from "@axe-core/playwright"
import { expect, test, type Page } from "@playwright/test"
import { z } from "zod"

const fixture = z
  .object({
    actor: z.string().min(1),
    target: z.string().min(1),
    from: z.iso.date(),
    to: z.iso.date(),
    adminToken: z.string().min(1),
    viewerToken: z.string().min(1),
  })
  .parse(JSON.parse(process.env.WF_AUDIT_FIXTURE ?? "{}"))

async function login(page: Page, token: string) {
  await page.goto("/")
  await expect(
    page.getByText("Sign in to manage workflow schedules.", { exact: true })
  ).toBeVisible()
  await page.evaluate(async (jwt) => {
    const path = "/scripts/browser-auth.ts"
    const { hydrateBrowserSession }: typeof import("../scripts/browser-auth") =
      await import(path)
    await hydrateBrowserSession(jwt)
  }, token)
  await page.goto("/admin/audit")
  await expect(page.getByRole("heading", { name: "Audit trail" })).toBeVisible()
}
async function accessible(page: Page) {
  const report = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
    .analyze()
  expect(
    report.violations.filter(
      (issue) => issue.impact === "critical" || issue.impact === "serious"
    )
  ).toEqual([])
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth
    )
  ).toBe(true)
}
test("Admin filters real audit by actor, target and UTC dates; pages, reloads, and revocation hides cached rows", async ({
  page,
}) => {
  await login(page, fixture.adminToken)
  await expect(page.getByRole("table")).toBeVisible()
  await page.getByLabel("Actor (exact)").fill(fixture.actor)
  await page.getByLabel("Target kind (exact)").fill("Machine")
  await page.getByLabel("Target ID (exact)").fill(fixture.target)
  await page.getByLabel("From date (UTC, inclusive)").fill(fixture.from)
  await page.getByLabel("Until date (UTC, exclusive)").fill(fixture.to)
  await page.getByRole("button", { name: "Filter audit", exact: true }).click()
  await expect(page).toHaveURL(new RegExp(`actor=${fixture.actor}`))
  await expect(page.locator("tbody tr")).toHaveCount(50)
  await expect(page.locator("tbody tr").first()).toContainText(fixture.actor)
  await expect(page.locator("tbody tr").first()).toContainText("old <script>")
  await expect(page.locator("tbody tr").first()).toContainText("new & safe")
  await accessible(page)
  await page.getByRole("button", { name: "Load more audit records" }).click()
  await expect(page.locator("tbody tr")).toHaveCount(51)
  await expect(
    page.getByRole("button", { name: "Load more audit records" })
  ).toHaveCount(0)
  const identities = await page.locator("tbody tr").allTextContents()
  expect(new Set(identities).size).toBe(51)
  await page.reload()
  await expect(page.locator("tbody tr")).toHaveCount(50)
  await expect(page.getByLabel("Actor (exact)")).toHaveValue(fixture.actor)
  await page.setViewportSize({ width: 375, height: 812 })
  await page.getByRole("button", { name: "Dark theme" }).click()
  await expect(page.locator("html")).toHaveClass(/dark/)
  await accessible(page)
  await page.route("**/api/v1/audit?**", async (route) => {
    const response = await route.fetch({
      headers: {
        ...route.request().headers(),
        authorization: `Bearer ${fixture.viewerToken}`,
      },
    })
    expect(response.status()).toBe(403)
    await route.fulfill({ response })
  })
  await page.getByRole("button", { name: "Refresh audit", exact: true }).click()
  await expect(page.getByRole("alert")).toContainText("403")
  await expect(page.getByRole("table")).toHaveCount(0)
  await expect(
    page.getByRole("button", { name: "Load more audit records" })
  ).toHaveCount(0)
})
test("Viewer direct audit navigation gets real 403 and no records", async ({
  page,
}) => {
  await login(page, fixture.viewerToken)
  await expect(page.getByRole("alert")).toContainText("403")
  await expect(page.getByRole("table")).toHaveCount(0)
})
