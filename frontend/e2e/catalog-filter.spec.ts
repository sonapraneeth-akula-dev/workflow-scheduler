import AxeBuilder from "@axe-core/playwright"
import { expect, test } from "@playwright/test"
import { z } from "zod"

const statuses = [
  "Queued",
  "Running",
  "Aborting",
  "Succeeded",
  "Failed",
  "Aborted",
  "TimedOut",
] as const

test("keyboard run filters, metadata grouping and virtualized results survive a copied URL", async ({
  page,
  request,
}, info) => {
  test.setTimeout(120_000)
  const workflowId = `filter-tests/search/${info.project.name}-dev`
  const from = new Date(Date.now() - 1000).toISOString()
  const ids: string[] = []
  for (let index = 0; index < 55; index++) {
    const response = await request.post("/api/v1/runs", {
      data: { workflowId },
    })
    expect(response.status(), await response.text()).toBe(202)
    ids.push(z.object({ runId: z.uuid() }).parse(await response.json()).runId)
  }
  const foreign = await request.post("/api/v1/runs", {
    data: { workflowId: `filter-tests/search/${info.project.name}-prod` },
  })
  expect(foreign.status()).toBe(202)
  let status: (typeof statuses)[number] = "Queued"
  await expect
    .poll(async () => {
      const response = await request.get("/api/v1/runs", {
        params: { workflowId, limit: 100 },
      })
      expect(response.status()).toBe(200)
      const rows = z
        .object({
          items: z.array(z.object({ id: z.uuid(), status: z.enum(statuses) })),
        })
        .parse(await response.json()).items
      if (rows.length) status = rows[0]!.status
      return rows.filter((row) => row.status === status).length
    })
    .toBe(55)
  const to = new Date(Date.now() + 1000).toISOString()
  const queries: URL[] = []
  page.on("request", (request) => {
    const url = new URL(request.url())
    if (request.method() === "GET" && url.pathname === "/api/v1/runs")
      queries.push(url)
  })
  await page.goto("/")
  await page.getByRole("link", { name: "Runs", exact: true }).focus()
  await page.keyboard.press("Enter")
  await page.getByRole("link", { name: "filter-tests", exact: true }).click()
  const form = page.getByRole("form", { name: "Run filters" })
  await form.getByLabel("Type", { exact: true }).focus()
  await expect(form.getByLabel("Type", { exact: true })).toBeFocused()
  await page.keyboard.type("search")
  await page.keyboard.press("Tab")
  await page.keyboard.type(workflowId)
  await page.keyboard.press("Tab")
  await page.keyboard.press("Tab")
  await page.keyboard.type(from)
  await page.keyboard.press("Tab")
  await page.keyboard.type(to)
  await page.keyboard.press("Tab")
  await expect(form.getByLabel("Run status")).toBeFocused()
  await page.keyboard.press("Home")
  for (let index = 0; index <= statuses.indexOf(status); index++)
    await page.keyboard.press("ArrowDown")
  await page.keyboard.press("Tab")
  await expect(form.getByLabel("Group loaded runs by")).toBeFocused()
  await page.keyboard.press("End")
  await page.keyboard.press("Tab")
  await page.keyboard.type("component")
  await page.keyboard.press("Tab")
  await page.keyboard.type('{"environment":"dev"}')
  await page.keyboard.press("Tab")
  await expect(form.getByRole("button", { name: "Search runs" })).toBeFocused()
  await page.keyboard.press("Enter")
  const results = page.getByRole("region", { name: "Run results", exact: true })
  await expect(results).toContainText("50 loaded runs")
  await expect(results).toContainText("component: api")
  await expect(results).not.toContainText(`${info.project.name}-prod`)
  expect(await results.locator("[data-run-row]").count()).toBeLessThanOrEqual(
    10
  )
  const copied = page.url()
  const url = new URL(copied)
  expect(url.pathname).toBe("/t/filter-tests/runs")
  expect(url.searchParams.get("workflowId")).toBe(workflowId)
  expect(url.searchParams.get("groupBy")).toBe("metadata")
  expect(url.searchParams.get("groupKey")).toBe("component")
  expect(queries.at(-1)?.searchParams.get("meta.environment")).toBe("dev")
  await page.getByRole("button", { name: "Load more runs" }).focus()
  await page.keyboard.press("Enter")
  await expect(results).toContainText("55 loaded runs")
  expect(await results.locator("[data-run-row]").count()).toBeLessThanOrEqual(
    10
  )
  const scroll = page.getByRole("region", { name: "Scrollable run results" })
  await scroll.evaluate((element) => {
    element.scrollTop = element.scrollHeight
  })
  await expect(results.getByRole("link").last()).toHaveAttribute(
    "href",
    `/runs/${ids[0]}`
  )
  await page.goto(copied)
  expect(new URL(page.url()).pathname).toBe("/t/filter-tests/runs")
  await expect(form.getByLabel("Type", { exact: true })).toHaveValue("search")
  await expect(form.getByLabel("Run status")).toHaveValue(status)
  await expect(form.getByLabel("Group loaded runs by")).toHaveValue("metadata")
  await expect(form.getByLabel("Metadata group key")).toHaveValue("component")
  await expect(form.getByLabel("Metadata filters (JSON)")).toHaveValue(
    '{"environment":"dev"}'
  )
  await expect(results).toContainText("component: api")
  await page.waitForLoadState("networkidle")
  const idle = queries.length
  await page.waitForTimeout(600)
  expect(queries).toHaveLength(idle)
  for (const dark of [false, true]) {
    await page.setViewportSize(
      dark ? { width: 375, height: 812 } : { width: 1440, height: 1080 }
    )
    await page.evaluate(
      (dark) => document.documentElement.classList.toggle("dark", dark),
      dark
    )
    const result = await new AxeBuilder({ page })
      .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
      .analyze()
    expect(
      result.violations.filter(
        (issue) => issue.impact === "critical" || issue.impact === "serious"
      )
    ).toEqual([])
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth
      )
    ).toBe(true)
  }
  await page.getByRole("button", { name: "Use accessible full list" }).focus()
  await page.keyboard.press("Enter")
  await expect(results.getByRole("link")).toHaveCount(50)
  await results.getByRole("link").first().focus()
  await page.keyboard.press("Enter")
  await expect(
    page.getByRole("heading", { name: "Run", exact: true })
  ).toBeVisible()
  await expect(page).toHaveURL(/\/runs\/[0-9a-f-]{36}$/)
})
