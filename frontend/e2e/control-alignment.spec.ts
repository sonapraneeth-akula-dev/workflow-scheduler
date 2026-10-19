import AxeBuilder from "@axe-core/playwright"
import { expect, test, type Locator, type Page } from "@playwright/test"
import { execFileSync } from "node:child_process"
import { readFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { resolve } from "node:path"
import { z } from "zod"
import { encodeDashboardId } from "../src/features/dashboards/api"
import { seedRendererReport } from "../scripts/seed-renderer-reports"

let fixture: { runId: string; seriesId: string; tableReportId: string }

test.beforeAll(async () => {
  const state = z
    .object({
      directory: z.string().regex(/^wf-ui-[a-zA-Z0-9_-]+$/),
      container: z.string().regex(/^wf-ui-[a-f0-9-]+$/),
      rendererRunId: z.uuid(),
      historySeriesId: z.uuid(),
    })
    .parse(
      JSON.parse(
        await readFile(new URL("../.test-stack.json", import.meta.url), "utf8")
      )
    )
  const report = await seedRendererReport(
    resolve(tmpdir(), state.directory),
    state.rendererRunId,
    (sql) =>
      execFileSync(
        "docker",
        [
          "exec",
          state.container,
          "psql",
          "-U",
          "workflow",
          "-d",
          "workflow",
          "-At",
          "-v",
          "ON_ERROR_STOP=1",
          "-c",
          sql,
        ],
        { encoding: "utf8", timeout: 20_000 }
      ).trim(),
    {
      kind: "csv",
      title: "Control alignment table",
      bytes: Buffer.from("name,value\nexample,1\n"),
    }
  )
  fixture = {
    runId: state.rendererRunId,
    seriesId: state.historySeriesId,
    tableReportId: report.id,
  }
})

async function checkControls(
  page: Page,
  controls: Locator,
  count: number,
  alignOnDesktop = true
) {
  await expect(controls).toHaveCount(count)
  await expect(controls.first()).toBeVisible()
  for (const colorScheme of ["light", "dark"] as const) {
    await page.emulateMedia({ colorScheme })
    if (colorScheme === "dark")
      await expect(page.locator("html")).toHaveClass(/dark/)
    else await expect(page.locator("html")).not.toHaveClass(/dark/)
    const bounds = await controls.evaluateAll((elements) =>
      elements.map((element) => {
        const rect = element.getBoundingClientRect()
        return { top: rect.top, height: rect.height }
      })
    )
    expect(bounds.map(({ height }) => height)).toEqual(Array(count).fill(32))
    if (alignOnDesktop && page.viewportSize()!.width >= 1280) {
      const tops = bounds.map(({ top }) => top)
      expect(Math.max(...tops) - Math.min(...tops)).toBeLessThanOrEqual(1)
    }
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth)
    ).toBeLessThanOrEqual(page.viewportSize()!.width)
  }
}

for (const width of [320, 768, 2000]) {
  test(`default controls stay evenly sized and aligned across pages at ${width}px`, async ({
    page,
  }, info) => {
    test.setTimeout(120_000)
    await page.setViewportSize({ width, height: 1000 })
    await test.step("service history filters and format controls", async () => {
      await page.goto("/t/ui-tests/services")
      await checkControls(
        page,
        page
          .getByRole("combobox")
          .or(page.getByRole("radio"))
          .or(page.getByRole("button", { name: "Refresh service history" })),
        5
      )
    })
    await test.step("run filters", async () => {
      await page.goto("/t/ui-tests/runs")
      const primary = page
        .getByRole("form", { name: "Run filters" })
        .locator(":scope > div")
        .first()
      await checkControls(page, primary.locator("input, select, button"), 5)
    })
    await test.step("workflow history filters", async () => {
      await page.goto("/t/ui-tests/release/w/workflow/history")
      await checkControls(
        page,
        page
          .getByRole("form", { name: "History filters" })
          .locator("input, select, button"),
        6
      )
    })
    await test.step("version comparison controls", async () => {
      await page.goto("/t/ui-tests/release/w/workflow/versions")
      await checkControls(
        page,
        page
          .getByRole("form", { name: "Compare workflow versions" })
          .locator("select, button"),
        3
      )
    })
    await test.step("metric dashboard controls", async () => {
      await page.goto(
        `/dashboards/workflow/${encodeDashboardId("ui-tests/release/workflow")}`
      )
      await checkControls(
        page,
        page
          .getByRole("region", { name: "Metric dashboard", exact: true })
          .locator(":scope > div")
          .first()
          .locator("select, button"),
        3
      )
    })
    await test.step("audit text and date filters", async () => {
      await page.goto("/admin/audit")
      await checkControls(
        page,
        page
          .getByRole("form", { name: "Audit filters" })
          .locator("input, button"),
        7
      )
    })
    await test.step("machine filters beside the multiline field", async () => {
      await page.goto("/machines")
      await checkControls(
        page,
        page
          .getByRole("form", { name: "Machine filters" })
          .locator("input, select, button"),
        5
      )
    })
    await test.step("series trends and report comparison", async () => {
      await page.goto(`/series/${fixture.seriesId}?view=trends`)
      await checkControls(
        page,
        page
          .getByRole("region", { name: "Series trends", exact: true })
          .locator(":scope > div")
          .first()
          .locator("select, button"),
        2
      )
      await page.goto(`/series/${fixture.seriesId}/compare`)
      await checkControls(
        page,
        page.locator("main form select, main form button"),
        5,
        false
      )
    })
    await test.step("schedule text inputs and timezone dropdown", async () => {
      await page.goto("/schedules/new")
      await checkControls(
        page,
        page
          .getByLabel("Workflow identity", { exact: true })
          .or(page.getByLabel("Schedule name", { exact: true }))
          .or(page.getByLabel("Timezone", { exact: true })),
        3,
        false
      )
    })
    await test.step("report renderer search, sort and direction", async () => {
      await page.goto(`/runs/${fixture.runId}/reports/${fixture.tableReportId}`)
      await checkControls(
        page,
        page
          .getByRole("region", { name: "Control alignment table table" })
          .locator(":scope > div")
          .first()
          .locator("input, select, button"),
        3
      )
    })
    await test.step("run report filters and system event filters", async () => {
      await page.goto(`/runs/${fixture.runId}`)
      await page.getByRole("tab", { name: "Reports", exact: true }).click()
      const reports = page.getByRole("region", {
        name: "Run reports",
        exact: true,
      })
      await checkControls(
        page,
        reports.locator(":scope > div").first().locator("select, button"),
        4
      )
      const audit = await new AxeBuilder({ page })
        .include('section[aria-label="Run reports"]')
        .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
        .analyze()
      expect(audit.violations).toEqual([])
      await page.screenshot({
        path: info.outputPath(`even-report-controls-${width}.png`),
        fullPage: true,
      })
      await page
        .getByRole("tab", { name: "System Events", exact: true })
        .click()
      await checkControls(
        page,
        page
          .getByRole("region", { name: "Run system events", exact: true })
          .locator("form input, form button"),
        5
      )
    })
  })
}
