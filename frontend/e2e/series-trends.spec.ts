import AxeBuilder from "@axe-core/playwright"
import { expect, test, type Page } from "@playwright/test"
import { z } from "zod"
import { seriesTrendsSchema } from "../src/features/series/api"

const fixture = z
  .object({
    seriesId: z.uuid(),
    emptySeriesId: z.uuid(),
    viewerToken: z.string().min(1),
    foreignToken: z.string().min(1),
  })
  .parse(JSON.parse(process.env.WF_TRENDS_FIXTURE ?? "{}"))

async function login(page: Page, token: string, seriesId = fixture.seriesId) {
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
  await page.goto(`/series/${seriesId}?view=trends`)
  await expect(
    page.getByRole("heading", { name: "Series history" })
  ).toBeVisible()
}

async function accessible(page: Page) {
  const results = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
    .analyze()
  expect(
    results.violations.filter(
      (issue) => issue.impact === "critical" || issue.impact === "serious"
    )
  ).toEqual([])
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth
    )
  ).toBe(true)
}

test("Viewer sees exact real trends, renders 90 days under 500ms, persists windows and hides stale data after real denial", async ({
  page,
  request,
}) => {
  await page.addInitScript(() => {
    const original = window.fetch
    window.fetch = async (...args) => {
      const response = await original(...args)
      const url = args[0] instanceof Request ? args[0].url : String(args[0])
      if (url.includes("/trends?window=90d") && response.ok) {
        window.fetch = original
        performance.mark("trends90-response")
        const observer = new MutationObserver(() => {
          const panel = document.querySelector(
            'section[aria-label="Series trends"]'
          )
          if (
            panel?.querySelector(
              '[aria-label="Scrollable daily trends table"] tbody'
            )?.childElementCount === 90 &&
            panel.querySelectorAll("canvas").length === 2
          ) {
            observer.disconnect()
            requestAnimationFrame(() =>
              requestAnimationFrame(() => {
                performance.measure("trends90-render", "trends90-response")
              })
            )
          }
        })
        observer.observe(document.body, { childList: true, subtree: true })
      }
      return response
    }
  })
  await login(page, fixture.viewerToken)
  const table = page.getByRole("table", {
    name: "Daily series trends (UTC), tabular chart alternative",
  })
  await expect(table.getByRole("row")).toHaveCount(31)
  await expect(
    page.getByRole("link", { name: "Trends", exact: true })
  ).toHaveAttribute("aria-current", "page")
  const actual = await request.get(
    `/api/v1/series/${fixture.seriesId}/trends?window=90d`,
    { headers: { Authorization: `Bearer ${fixture.viewerToken}` } }
  )
  expect(actual.status()).toBe(200)
  const expected = seriesTrendsSchema.parse(await actual.json())
  const responseSeen = page.waitForResponse(
    (response) =>
      response.url().includes("/trends?window=90d") && response.status() === 200
  )
  await page.getByLabel("Trend window").selectOption("90d")
  await responseSeen
  await expect(table.getByRole("row")).toHaveCount(91)
  await expect(
    page.getByRole("img", { name: "Daily duration (ms): bar chart" })
  ).toBeVisible()
  await expect(page.locator("canvas")).toHaveCount(2)
  await expect
    .poll(() =>
      page.evaluate(
        () => performance.getEntriesByName("trends90-render").length
      )
    )
    .toBe(1)
  const renderMs = await page.evaluate(
    () => performance.getEntriesByName("trends90-render")[0]!.duration
  )
  console.log(
    `Real 90-day trends response-to-render: ${renderMs.toFixed(2)}ms.`
  )
  expect(renderMs).toBeLessThan(500)
  await expect(page).toHaveURL(/trendWindow=90d/)
  await expect(
    page.getByText("Completed runs", { exact: true }).locator("..")
  ).toContainText(String(expected.totals.runs))
  await expect(
    page.getByText("p50 duration", { exact: true }).first().locator("..")
  ).toContainText(expected.totals.p50Ms!.toLocaleString("en-US"))
  await expect(table.locator("tbody tr").first()).toContainText(expected.from)
  await expect(table.locator("tbody tr").last()).toContainText(
    expected.days.at(-1)!.day
  )
  await accessible(page)
  await page.reload()
  await expect(page.getByLabel("Trend window")).toHaveValue("90d")
  await expect(table.getByRole("row")).toHaveCount(91)
  await page.getByLabel("Trend window").selectOption("7d")
  await expect(table.getByRole("row")).toHaveCount(8)
  await page.setViewportSize({ width: 375, height: 812 })
  await page.getByRole("button", { name: "Dark theme" }).click()
  await expect(page.locator("html")).toHaveClass(/dark/)
  await accessible(page)
  await page.route("**/api/v1/series/*/trends?**", async (route) => {
    const denied = await route.fetch({
      headers: {
        ...route.request().headers(),
        authorization: `Bearer ${fixture.foreignToken}`,
      },
    })
    expect(denied.status()).toBe(403)
    await route.fulfill({ response: denied })
  })
  await page
    .getByRole("button", { name: "Refresh trends", exact: true })
    .click()
  await expect(page.getByRole("alert")).toContainText(/403|caller|permission/i)
  await expect(table).toHaveCount(0)
  await expect(page.locator("canvas")).toHaveCount(0)
})

test("empty series stays explicitly unmeasured; foreign Viewer receives real permission denial without cached charts", async ({
  page,
  browser,
}) => {
  await login(page, fixture.viewerToken, fixture.emptySeriesId)
  await expect(
    page.getByText("No completed runs in this window.", { exact: true })
  ).toBeVisible()
  await expect(page.locator("canvas")).toHaveCount(0)
  await expect(page.getByRole("table").getByRole("row")).toHaveCount(31)
  await page.getByRole("link", { name: "Run history", exact: true }).click()
  await expect(
    page.getByText("No matching history.", { exact: true })
  ).toBeVisible()
  await expect(page.getByLabel("Trend window")).toHaveCount(0)
  await page.getByRole("link", { name: "Trends", exact: true }).click()
  await expect(
    page.getByText("No completed runs in this window.", { exact: true })
  ).toBeVisible()
  await accessible(page)
  const foreign = await browser.newContext({
    baseURL: new URL(page.url()).origin,
  })
  try {
    const denied = await foreign.newPage()
    await login(denied, fixture.foreignToken)
    await expect(denied.getByRole("alert")).toContainText(
      /403|caller|permission/i
    )
    await expect(denied.getByRole("table")).toHaveCount(0)
    await expect(denied.locator("canvas")).toHaveCount(0)
  } finally {
    await foreign.close()
  }
})
