import AxeBuilder from "@axe-core/playwright"
import { expect, test, type Page } from "@playwright/test"
import { z } from "zod"

const fixture = z
  .object({
    workerId: z.uuid(),
    workerName: z.string().min(1),
    adminToken: z.string().min(1),
    viewerToken: z.string().min(1),
  })
  .parse(JSON.parse(process.env.WF_SYSTEM_FIXTURE ?? "{}"))
const headers = { authorization: `Bearer ${fixture.adminToken}` }

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
  await page.goto("/admin/system")
  await expect(
    page.getByRole("heading", { name: "Live system", exact: true })
  ).toBeVisible()
}

test("Admin observes real inventory changes, pause and reduced motion; outages hide cached health", async ({
  page,
  request,
}, testInfo) => {
  test.setTimeout(90_000)
  expect(
    (await request.post("/api/fixture/system-work/false", { headers })).status()
  ).toBe(204)
  await login(page, fixture.adminToken)
  const workers = page.getByRole("table", { name: "Workers and assigned work" })
  const worker = workers.getByRole("row").filter({
    has: page.getByRole("link", { name: fixture.workerName, exact: true }),
  })
  await expect(worker).toBeVisible()
  await expect(worker.getByRole("cell").nth(2)).toHaveText("0 / 0")
  expect(
    (await request.post("/api/fixture/system-work/true", { headers })).status()
  ).toBe(204)
  await expect(worker.getByRole("cell").nth(2)).toHaveText("3 / 3")
  await expect(worker.getByRole("cell").nth(3)).toHaveText("2")
  await expect(worker.getByRole("cell").nth(4)).toHaveText("1")
  await expect(worker.getByRole("cell").nth(5)).toHaveText("1")
  const instances = page.getByRole("table", {
    name: "API and controller processes",
  })
  await expect(
    instances.getByRole("cell", { name: "API + controller No active lease" })
  ).toBeVisible()
  await expect(
    page.getByText("No active controller lease", { exact: true })
  ).toBeVisible()

  await page
    .getByRole("button", { name: "Pause live updates", exact: true })
    .click()
  await expect(page.getByRole("status", { name: "" })).toHaveText(
    "Paused snapshot; not live"
  )
  const pausedAt = await worker.getByRole("cell").last().textContent()
  expect(
    (await request.post("/api/fixture/system-work/false", { headers })).status()
  ).toBe(204)
  await page.waitForTimeout(2200)
  await expect(worker.getByRole("cell").nth(2)).toHaveText("3 / 3")
  await expect(worker.getByRole("cell").last()).toHaveText(pausedAt ?? "")
  await expect(page.locator(".kardia-system-flow")).toHaveCount(0)
  await page
    .getByRole("button", { name: "Resume live updates", exact: true })
    .click()
  await expect(worker.getByRole("cell").nth(2)).toHaveText("0 / 0")
  await page.emulateMedia({ reducedMotion: "reduce" })
  await page
    .getByRole("button", { name: "Refresh system", exact: true })
    .click()
  await expect
    .poll(async () =>
      page
        .locator(".kardia-system-flow")
        .evaluateAll((paths) =>
          paths.every((path) => getComputedStyle(path).animationName === "none")
        )
    )
    .toBe(true)

  for (const width of [1440, 1024, 768, 390]) {
    await page.setViewportSize({ width, height: 1000 })
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth
      )
    ).toBe(true)
  }
  const topology = page.getByRole("region", {
    name: "Observed application flow",
  })
  await topology.focus()
  await page.keyboard.press("ArrowRight")
  await expect
    .poll(() => topology.evaluate((element) => element.scrollLeft))
    .toBeGreaterThan(0)
  await page
    .getByRole("button", { name: "Pause live updates", exact: true })
    .click()
  await expect(
    page.getByRole("button", { name: "Refresh system", exact: true })
  ).toBeEnabled()
  await expect(
    page.getByRole("button", { name: "Refresh system", exact: true })
  ).toHaveCSS("opacity", "1")
  for (const theme of ["light", "dark"]) {
    const toggle = page.getByRole("button", {
      name: theme === "dark" ? "Dark theme" : "Light theme",
      exact: true,
    })
    if (await toggle.count()) await toggle.click()
    await expect
      .poll(() =>
        page
          .locator("body")
          .evaluate((element) => getComputedStyle(element).color)
      )
      .toBe(theme === "dark" ? "rgb(233, 239, 250)" : "rgb(23, 36, 59)")
    const report = await new AxeBuilder({ page })
      .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
      .analyze()
    expect(
      report.violations.filter(
        (issue) => issue.impact === "critical" || issue.impact === "serious"
      )
    ).toEqual([])
  }
  await page
    .getByRole("button", { name: "Resume live updates", exact: true })
    .click()
  await page.setViewportSize({ width: 1440, height: 1000 })
  await testInfo.attach("live-system", {
    body: await page.screenshot({ fullPage: true }),
    contentType: "image/png",
  })

  await page.route("**/api/v1/admin/system", (route) =>
    route.fulfill({
      status: 503,
      contentType: "application/problem+json",
      body: JSON.stringify({ title: "System inventory unavailable" }),
    })
  )
  await page
    .getByRole("button", { name: "Refresh system", exact: true })
    .click()
  await expect(page.getByRole("alert")).toContainText(
    "System inventory unavailable"
  )
  await expect(
    page.getByRole("table", { name: "Workers and assigned work" })
  ).toHaveCount(0)
  await expect(page.locator(".kardia-system-flow")).toHaveCount(0)
  await page.unroute("**/api/v1/admin/system")
  await page
    .getByRole("button", { name: "Refresh system", exact: true })
    .click()
  await expect(workers).toBeVisible()
})

test("Viewer cannot query or display global topology", async ({
  page,
  request,
}) => {
  expect(
    (
      await request.get("/api/v1/admin/system", {
        headers: { authorization: `Bearer ${fixture.viewerToken}` },
      })
    ).status()
  ).toBe(403)
  await login(page, fixture.viewerToken)
  await expect(page.getByRole("alert")).toContainText("Global Admin")
  await expect(page.getByRole("table")).toHaveCount(0)
  await expect(
    page.getByRole("button", { name: "Refresh system" })
  ).toBeDisabled()
})
