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
  await page
    .getByRole("link", { name: "ui-tests / release", exact: true })
    .click()
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

test("team presentation orders types and applies metadata grouping, columns, filters and labels without affecting another team", async ({
  page,
}) => {
  const filteredReads: {
    team: string | null
    defaults: string | null
    environment: string | null
  }[] = []
  page.on("request", (request) => {
    const url = new URL(request.url())
    if (url.pathname === "/api/v1/workflows")
      filteredReads.push({
        team: url.searchParams.get("team"),
        defaults: url.searchParams.get("defaults"),
        environment: url.searchParams.get("meta.environment"),
      })
  })
  await page.goto("/")
  await page.getByRole("link", { name: "presentation-a", exact: true }).click()
  await expect(
    page.getByRole("link", { name: /^presentation-a \/ (perf|release)$/ })
  ).toHaveText(["presentation-a / perf", "presentation-a / release"])
  await expect(
    page.getByRole("region", { name: "Default metadata filters" })
  ).toContainText("Target environment:")
  await page
    .getByRole("link", { name: "presentation-a / release", exact: true })
    .click()
  const table = page.getByRole("table")
  await expect(table).toContainText("dev-workflow")
  await expect(table).not.toContainText("prod-workflow")
  await expect(
    page.getByRole("columnheader", { name: "Component name" })
  ).toBeVisible()
  await expect(
    page.getByRole("cell", { name: "api", exact: true })
  ).toBeVisible()
  await expect(table).toContainText("Target environment: dev")
  await accessible(page)
  await page.getByRole("button", { name: "Dark theme" }).click()
  await page.setViewportSize({ width: 375, height: 812 })
  await accessible(page)
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth
    )
  ).toBe(true)
  await page.getByRole("link", { name: "Teams", exact: true }).click()
  await page.getByRole("link", { name: "presentation-b", exact: true }).click()
  await expect(
    page.getByRole("link", { name: /^presentation-b \/ (perf|release)$/ })
  ).toHaveText(["presentation-b / release", "presentation-b / perf"])
  await expect(
    page.getByRole("region", { name: "Default metadata filters" })
  ).toHaveCount(0)
  await page
    .getByRole("link", { name: "presentation-b / release", exact: true })
    .click()
  await expect(table).toContainText("dev-workflow")
  await expect(table).toContainText("prod-workflow")
  await expect(
    page.getByRole("columnheader", { name: "Component name" })
  ).toHaveCount(0)
  await expect(table).not.toContainText("Target environment:")
  expect(
    filteredReads.some(
      (read) =>
        read.team === "presentation-a" &&
        read.defaults === "true" &&
        read.environment === null
    )
  ).toBe(true)
  expect(
    filteredReads
      .filter((read) => read.team === "presentation-b")
      .every((read) => read.defaults === "true" && read.environment === null)
  ).toBe(true)
  await accessible(page)
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth
    )
  ).toBe(true)
})
