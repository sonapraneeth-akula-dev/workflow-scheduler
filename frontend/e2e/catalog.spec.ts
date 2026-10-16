import AxeBuilder from "@axe-core/playwright"
import { expect, test, type Page } from "@playwright/test"
import { z } from "zod"

async function accessible(page: Page) {
  await page.evaluate(() =>
    Promise.allSettled(
      document.getAnimations().map((animation) => animation.finished)
    )
  )
  const result = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
    .analyze()
  expect(
    result.violations.filter(
      (issue) => issue.impact === "critical" || issue.impact === "serious"
    )
  ).toEqual([])
}

test("home reaches workflows in two clicks and committed run changes update type and team without polling", async ({
  page,
  request,
}, info) => {
  const name = `catalog-${info.project.name}`
  const workflowId = `ui-tests/release/${name}`
  let workflowReads = 0
  let typeReads = 0
  let sockets = 0
  page.on("request", (request) => {
    const path = new URL(request.url()).pathname
    if (path === "/api/v1/workflows") workflowReads++
    if (path === "/api/v1/teams/ui-tests/types") typeReads++
  })
  page.on("websocket", (socket) => {
    if (new URL(socket.url()).pathname === "/hubs/live/v1") sockets++
  })
  await page.goto("/")
  await page.getByRole("link", { name: "ui-tests", exact: true }).click()
  await expect(page).toHaveURL("/t/ui-tests")
  await expect(page.getByText("UI integration", { exact: true })).toBeVisible()
  await expect(
    page.getByText("Live updates connected.", { exact: true })
  ).toBeVisible()
  await accessible(page)
  await page.getByRole("link", { name: "release", exact: true }).click()
  await expect(page).toHaveURL("/t/ui-tests/release")
  const row = page.getByRole("row").filter({ hasText: workflowId })
  await expect(row).toContainText("Never run")
  await expect(row).toContainText("1.0.0")
  await expect(
    page.getByText("Live updates connected.", { exact: true })
  ).toBeVisible()
  await page.waitForLoadState("networkidle")
  const idleReads = workflowReads
  await page.waitForTimeout(600)
  expect(workflowReads).toBe(idleReads)
  const response = await request.post("/api/v1/runs", { data: { workflowId } })
  expect(response.status()).toBe(202)
  const run = z.object({ runId: z.uuid() }).parse(await response.json())
  await expect(row).toContainText(run.runId)
  await expect(row).not.toContainText("Never run")
  expect(workflowReads).toBeGreaterThan(idleReads)
  await accessible(page)
  await page.getByRole("button", { name: "Dark theme" }).click()
  await page.setViewportSize({ width: 375, height: 812 })
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth
    )
  ).toBe(true)
  await accessible(page)
  await page.getByRole("link", { name: "ui-tests", exact: true }).click()
  await expect(
    page.getByText("Live updates connected.", { exact: true })
  ).toBeVisible()
  await expect(page.getByText("Queued", { exact: true })).toBeVisible()
  const before = typeReads
  const next = await request.post("/api/v1/runs", {
    data: { workflowId: "ui-tests/release/workflow" },
  })
  expect(next.status()).toBe(202)
  await expect.poll(() => typeReads).toBeGreaterThan(before)
  expect(sockets).toBeGreaterThan(0)
})
