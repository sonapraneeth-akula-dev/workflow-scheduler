import AxeBuilder from "@axe-core/playwright"
import { expect, test, type Page } from "@playwright/test"
import { z } from "zod"
import { dashboardSchema } from "../src/features/dashboards/api"

const fixture = z
  .object({
    workflow: z.string(),
    workflowId: z.string(),
    nodeId: z.string(),
    scheduleId: z.uuid(),
    machineId: z.uuid(),
    emptyId: z.string(),
    foreignWorkflow: z.string(),
    viewerToken: z.string().min(1),
    foreignToken: z.string().min(1),
  })
  .parse(JSON.parse(process.env.WF_DASHBOARD_FIXTURE ?? "{}"))
const path = (
  scope = "workflow",
  id = fixture.workflowId,
  search = "window=30d&top=1"
) => `/dashboards/${scope}/${id}?${search}`

async function login(page: Page, token = fixture.viewerToken, target = path()) {
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
  await page.goto(target)
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

test("workflow rankings have exact accessible charts/tables and a slow node opens filtered real runs", async ({
  page,
  request,
}) => {
  await login(page)
  const table = page.getByRole("table", {
    name: "Slowest nodes: values and run drill-down",
  })
  await expect(table.getByRole("row")).toHaveCount(2)
  await expect(table.locator("tbody")).toContainText("96,500 ms")
  await expect(page.locator("canvas")).toHaveCount(5)
  const oracle = await request.get(
    `/api/v1/dashboards/workflow/${fixture.workflowId}?window=30d&top=1`,
    { headers: { Authorization: `Bearer ${fixture.viewerToken}` } }
  )
  expect(oracle.status()).toBe(200)
  const dashboard = dashboardSchema.parse(await oracle.json())
  const expected = dashboard.rankings.slowest[0]!
  expect(dashboard.trends?.total).toMatchObject({
    runs: 2,
    completed: 2,
    durationCount: 2,
    status: { succeeded: 1, failed: 1 },
    p50Ms: 65000,
    p90Ms: 93000,
    p99Ms: 99300,
    p999Ms: 99930,
  })
  const latency = page.getByRole("region", {
    name: "Workflow execution latency (ms)",
    exact: true,
  })
  await expect(latency.getByRole("img")).toHaveAttribute(
    "aria-label",
    "Workflow execution latency (ms): line chart"
  )
  await latency.getByText("Chart data table", { exact: true }).click()
  await expect(latency.getByRole("table")).toContainText("UTC creation day")
  await expect(latency.getByRole("table")).toContainText("p99.9 (p999)")
  const status = page.getByRole("region", {
    name: "Workflow status counts",
    exact: true,
  })
  await expect(status.getByRole("img")).toHaveAttribute(
    "aria-label",
    "Workflow status counts: bar chart"
  )
  await status.getByText("Chart data table", { exact: true }).click()
  await expect(status.getByRole("table")).toContainText("Succeeded")
  await expect(status.getByRole("table")).toContainText("Failed")
  await expect(
    table.getByRole("link", { name: "Latest contribution", exact: true })
  ).toHaveAttribute("href", expected.runHref)
  await accessible(page)
  await page.getByLabel("Dashboard window").selectOption("90d")
  await expect(page).toHaveURL(/window=90d/)
  await expect(table.locator("tbody")).toContainText("93,000 ms")
  await page.getByLabel("Top rankings").selectOption("5")
  await expect(table.getByRole("row")).toHaveCount(3)
  await page.reload()
  await expect(page.getByLabel("Dashboard window")).toHaveValue("90d")
  await expect(page.getByLabel("Top rankings")).toHaveValue("5")
  await expect(table.getByRole("row")).toHaveCount(3)
  await page.setViewportSize({ width: 375, height: 812 })
  await page.getByRole("button", { name: "Dark theme" }).click()
  await accessible(page)
  await page.getByLabel("Dashboard window").selectOption("30d")
  await expect(table.locator("tbody")).toContainText("96,500 ms")
  await table.getByRole("link", { name: "alpha", exact: true }).click()
  await expect(
    page.getByRole("heading", { name: / runs$/, exact: true })
  ).toBeVisible()
  const search = new URL(page.url()).searchParams
  expect(search.get("workflowId")).toBe(fixture.workflow)
  expect(search.get("from")).toBe("2026-09-18T00:00:00Z")
  expect(search.get("to")).toBe("2026-10-18T00:00:00Z")
  await expect(page.locator("[data-run-row]")).toHaveCount(2)
  await expect(page.locator("[data-run-row]")).toContainText([
    fixture.workflow,
    fixture.workflow,
  ])
  await expect(
    page.getByText(fixture.foreignWorkflow, { exact: true })
  ).toHaveCount(0)
})

test("schedule, node and machine views preserve scope and current Viewer isolation without inventory access", async ({
  page,
  request,
}) => {
  await login(page)
  for (const [scope, id] of [
    ["schedule", fixture.scheduleId],
    ["node", fixture.nodeId],
    ["machine", fixture.machineId],
  ]) {
    await page.goto(path(scope, id, "window=90d&top=5"))
    await expect(
      page.getByRole("heading", { name: `${scope} dashboard`, exact: true })
    ).toBeVisible()
    const table = page.getByRole("table", {
      name: "Slowest nodes: values and run drill-down",
    })
    await expect(table.getByRole("row")).toHaveCount(scope === "node" ? 2 : 3)
    await expect(table.locator("tbody")).toContainText("93,000 ms")
    await expect(
      page.getByText(fixture.foreignWorkflow, { exact: true })
    ).toHaveCount(0)
    const oracle = await request.get(
      `/api/v1/dashboards/${scope}/${id}?window=90d&top=5`,
      { headers: { Authorization: `Bearer ${fixture.viewerToken}` } }
    )
    expect(oracle.status()).toBe(200)
    const expected = dashboardSchema.parse(await oracle.json())
    expect(expected.observationKind).toBe(
      scope === "machine" ? "TaskAttempt" : "Node"
    )
    expect(expected.rankings.slowest.map((row) => row.workflowId)).toEqual(
      Array(expected.rankings.slowest.length).fill(fixture.workflow)
    )
    await expect(
      table.getByRole("link", { name: "Latest contribution" }).first()
    ).toHaveAttribute("href", expected.rankings.slowest[0]!.runHref)
    await accessible(page)
  }
  const inventory = await request.get("/api/v1/permissions/machines?id=*", {
    headers: { Authorization: `Bearer ${fixture.viewerToken}` },
  })
  expect(inventory.status()).toBe(403)
  await page.setViewportSize({ width: 375, height: 812 })
  await page.getByRole("button", { name: "Dark theme" }).click()
  await accessible(page)
  const table = page.getByRole("table", {
    name: "Slowest nodes: values and run drill-down",
  })
  await table.getByRole("link", { name: "Node dashboard" }).first().click()
  await expect(
    page.getByRole("heading", { name: "node dashboard", exact: true })
  ).toBeVisible()
  await expect(table.getByRole("row")).toHaveCount(2)
})

test("empty data stays unmeasured and real authorization denial hides cached rankings without retrying", async ({
  page,
  browser,
}) => {
  await login(page, fixture.viewerToken, path("workflow", fixture.emptyId))
  await expect(
    page.getByText("No completed observations in this window.", { exact: true })
  ).toBeVisible()
  await expect(page.getByRole("table")).toHaveCount(0)
  await expect(page.locator("canvas")).toHaveCount(0)
  await accessible(page)
  await page.goto(path())
  await expect(
    page.getByRole("table", {
      name: "Slowest nodes: values and run drill-down",
    })
  ).toBeVisible()
  let denied = 0
  await page.route("**/api/v1/dashboards/**", async (route) => {
    const response = await route.fetch({
      headers: {
        ...route.request().headers(),
        authorization: `Bearer ${fixture.foreignToken}`,
      },
    })
    expect(response.status()).toBe(403)
    denied++
    await route.fulfill({ response })
  })
  await page.getByRole("button", { name: "Refresh dashboard" }).click()
  await expect(page.getByRole("alert")).toContainText(
    /403|caller|permission|Forbidden/i
  )
  await expect(page.getByRole("table")).toHaveCount(0)
  await expect(page.locator("canvas")).toHaveCount(0)
  expect(denied).toBe(1)
  await accessible(page)
  const foreign = await browser.newContext({
    baseURL: new URL(page.url()).origin,
  })
  try {
    const isolated = await foreign.newPage()
    await login(isolated, fixture.foreignToken)
    await expect(isolated.getByRole("alert")).toContainText(
      /403|caller|permission|Forbidden/i
    )
    await expect(isolated.getByRole("table")).toHaveCount(0)
    await expect(isolated.locator("canvas")).toHaveCount(0)
  } finally {
    await foreign.close()
  }
})
