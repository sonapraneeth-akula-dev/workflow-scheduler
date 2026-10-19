import { expect, test, type Page } from "@playwright/test"
import AxeBuilder from "@axe-core/playwright"
import { z } from "zod"
import { catalogWorkflowSchema } from "../src/features/catalog/api"

async function openWorkflowHistory(page: Page) {
  const response = await page.request.get("/api/v1/workflows", {
    params: { team: "ui-tests", type: "release", limit: 50 },
  })
  expect(response.status()).toBe(200)
  const { items } = z
    .object({ items: z.array(catalogWorkflowSchema) })
    .parse(await response.json())
  const workflow = items.find((item) => item.latestRun !== null)
  if (!workflow) throw new Error("The catalogue has no recorded workflow run.")
  await page.goto(
    `/t/${workflow.team}/${workflow.type}/w/${workflow.name}/history`
  )
  await expect(
    page.getByRole("heading", { name: "Workflow history", exact: true })
  ).toBeVisible()
  await expect(page.getByRole("table")).toBeVisible()
  await expect(
    page.getByRole("columnheader", { name: "Run ID", exact: true })
  ).toBeVisible()
  await expect(
    page.getByRole("columnheader", { name: /Variables/ })
  ).toHaveCount(0)
}

test("schedule-series histories are linked once outside run IDs and work on mobile", async ({
  page,
}) => {
  const response = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === "/api/v1/runs/history" &&
      response.ok()
  )
  await page.goto("/t/ui-tests/release/w/workflow/history")
  const { items } = z
    .object({
      items: z.array(z.object({ seriesId: z.uuid().nullable() })),
    })
    .parse(await (await response).json())
  const ids = [
    ...new Set(items.flatMap(({ seriesId }) => (seriesId ? [seriesId] : []))),
  ]
  expect(ids.length).toBeGreaterThan(0)
  const navigation = page.getByRole("navigation", {
    name: "Schedule series histories",
  })
  await expect(navigation.getByRole("link")).toHaveCount(ids.length)
  for (const id of ids) {
    await expect(navigation.locator(`a[href="/series/${id}"]`)).toHaveAttribute(
      "title",
      `Runs and schedule events for series ${id}`
    )
  }
  await expect(
    page.getByRole("table").getByRole("link", { name: /series history/i })
  ).toHaveCount(0)
  await page.setViewportSize({ width: 375, height: 812 })
  expect(
    await page.evaluate(() => document.documentElement.scrollWidth)
  ).toBeLessThanOrEqual(375)
  expect(
    (
      await new AxeBuilder({ page })
        .include('nav[aria-label="Schedule series histories"]')
        .withTags(["wcag2a", "wcag2aa", "wcag21aa"])
        .analyze()
    ).violations
  ).toEqual([])
  await navigation.getByRole("link").first().focus()
  await page.keyboard.press("Enter")
  await expect(
    page.getByRole("heading", { name: "Series history", exact: true })
  ).toBeVisible()
  await expect(page.getByRole("table")).toBeVisible()
  await expect(navigation).toHaveCount(0)
})

test("executed versions show semantic labels while preserving immutable version links", async ({
  page,
}) => {
  const response = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === "/api/v1/runs/history" &&
      response.ok()
  )
  await openWorkflowHistory(page)
  const history = z
    .object({
      items: z.array(
        z.object({
          runId: z.uuid().nullable(),
          versionId: z.uuid().nullable(),
          version: z.string().nullable(),
        })
      ),
    })
    .parse(await (await response).json())
  const run = history.items.find(
    (row) => row.runId && row.versionId && row.version
  )
  if (!run?.runId || !run.versionId || !run.version)
    throw new Error("The published workflow has no recorded executed version.")
  expect(run.version).toMatch(/^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)*$/)
  const row = page
    .getByRole("table")
    .getByRole("row")
    .filter({ has: page.getByRole("link", { name: run.runId, exact: true }) })
  const version = row.getByRole("link", { name: run.version, exact: true })
  await expect(
    row.getByRole("rowheader").getByRole("link", {
      name: run.runId,
      exact: true,
    })
  ).toHaveAttribute("href", `/runs/${run.runId}`)
  await expect(row.getByRole("rowheader").locator("time")).toHaveCount(0)
  await expect(version).toHaveAttribute(
    "href",
    new RegExp(`/versions/${run.versionId}$`)
  )
  await expect(version).toHaveAttribute(
    "title",
    `Immutable version ID: ${run.versionId}`
  )
  await expect(row).not.toContainText(run.versionId)
  await version.click()
  await expect(page).toHaveURL(new RegExp(`/versions/${run.versionId}$`))
  await expect(
    page.getByRole("heading", { name: "Task Plan", exact: true })
  ).toBeVisible()
})

for (const width of [320, 768, 2000]) {
  test(`workflow header stays compact and usable at ${width}px`, async ({
    page,
  }) => {
    await page.setViewportSize({ width, height: 1000 })
    await openWorkflowHistory(page)
    const summary = page.getByRole("region", {
      name: "Workflow history summary",
    })
    await expect(
      summary.getByRole("link", { name: "Workflow versions" })
    ).toBeVisible()
    await expect(
      summary.getByRole("link", { name: "Metrics dashboard" })
    ).toBeVisible()
    await expect(
      summary.getByRole("button", { name: "Trigger run", exact: true })
    ).toBeVisible()
    await expect(
      summary.getByRole("button", { name: "Subscribe", exact: true })
    ).toBeVisible()
    expect((await summary.boundingBox())!.height).toBeLessThan(
      width < 640 ? 200 : 140
    )
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth)
    ).toBeLessThanOrEqual(width)
    if (width >= 1280) {
      expect(
        (await summary
          .getByRole("button", { name: "Trigger run", exact: true })
          .boundingBox())!.width
      ).toBeLessThan(200)
      expect((await summary.boundingBox())!.height).toBeLessThan(80)
    }
  })
}

test("workflow header links open registered releases and metric content", async ({
  page,
}) => {
  await openWorkflowHistory(page)
  await page
    .getByRole("link", { name: "Workflow versions", exact: true })
    .click()
  await expect(
    page.getByRole("heading", { name: "Versions", exact: true })
  ).toBeVisible()
  await expect(page.getByText("Loading version history...")).toHaveCount(0)
  await expect(
    page.getByText("Active version:", { exact: false })
  ).toBeVisible()
  await expect(page.getByRole("alert")).toHaveCount(0)
  await page.goBack()
  await page
    .getByRole("link", { name: "Metrics dashboard", exact: true })
    .click()
  await expect(
    page.getByRole("heading", { name: "workflow dashboard", exact: true })
  ).toBeVisible()
  await expect(
    page.getByText("Checking dashboard access and loading rankings...")
  ).toHaveCount(0)
  await expect(
    page
      .getByRole("region", { name: "Metric dashboard" })
      .getByRole("status")
      .first()
  ).toContainText("(UTC). Top")
  await expect(page.getByRole("alert")).toHaveCount(0)
})

for (const width of [320, 768, 1440, 2000]) {
  test(`history filters align labels and controls without overflow at ${width}px`, async ({
    page,
  }) => {
    await page.setViewportSize({ width, height: 1000 })
    await openWorkflowHistory(page)
    const form = page.getByRole("form", {
      name: "History filters",
      exact: true,
    })
    const controls = form.locator("select, input")
    await expect(controls).toHaveCount(4)
    const bounds = await controls.evaluateAll((elements) =>
      elements.map((element) => {
        const control = element.getBoundingClientRect()
        const label = document
          .querySelector(`label[for="${CSS.escape(element.id)}"]`)!
          .getBoundingClientRect()
        return {
          control: { y: control.y, height: control.height },
          label: { y: label.y, bottom: label.bottom },
        }
      })
    )
    expect(bounds.every(({ control }) => control.height === 36)).toBe(true)
    expect(bounds.every(({ control, label }) => label.bottom < control.y)).toBe(
      true
    )
    if (width >= 1280) {
      expect(new Set(bounds.map(({ label }) => label.y)).size).toBe(1)
      expect(new Set(bounds.map(({ control }) => control.y)).size).toBe(1)
      const submit = await form
        .getByRole("button", { name: "Filter history", exact: true })
        .boundingBox()
      expect(submit!.y).toBe(bounds[0]!.control.y)
      expect(submit!.height).toBe(36)
    }
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth)
    ).toBeLessThanOrEqual(width)
    expect(
      (
        await new AxeBuilder({ page })
          .include('form[aria-label="History filters"]')
          .withTags(["wcag2a", "wcag2aa", "wcag21aa"])
          .analyze()
      ).violations
    ).toEqual([])
  })
}
