import AxeBuilder from "@axe-core/playwright"
import { expect, test, type Page } from "@playwright/test"
import { z } from "zod"

const fixture = z
  .object({
    source: z.uuid(),
    workflowId: z.string(),
    version: z.uuid(),
    build: z.record(z.string(), z.unknown()),
    eu: z.record(z.string(), z.unknown()),
  })
  .parse(JSON.parse(process.env.WF_RERUN_EXECUTION_FIXTURE ?? "{}"))

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
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth
    )
  ).toBe(true)
}
test("Scenario A rerun executes US and parallel smoke with original Build/EU receipts; lineage opens unchanged original outputs", async ({
  page,
}) => {
  test.setTimeout(90_000)
  await page.goto(`/runs/${fixture.source}`)
  await expect(
    page.getByText(fixture.workflowId, { exact: true })
  ).toBeVisible()
  await expect(
    page
      .getByRole("region", { name: "Run controls" })
      .getByText("Failed", { exact: true })
  ).toBeVisible()
  await page
    .getByRole("button", { name: "Re-run from failed", exact: true })
    .click()
  const dialog = page.getByRole("alertdialog")
  await expect(dialog).toContainText(fixture.version)
  await dialog
    .getByRole("button", { name: "Confirm rerun", exact: true })
    .click()
  const link = dialog.getByRole("link", { name: /^Open rerun / })
  const destination = await link.getAttribute("href")
  expect(destination).toMatch(/^\/runs\/[0-9a-f-]{36}$/)
  await link.click()
  await expect(
    page
      .getByRole("region", { name: "Run controls" })
      .getByText("Succeeded", { exact: true })
  ).toBeVisible({ timeout: 60_000 })
  const lineage = page.getByRole("region", { name: "Run lineage" })
  await expect(lineage.getByText("Reused", { exact: true })).toHaveCount(2)
  await expect(
    lineage.getByRole("link", { name: fixture.source, exact: true })
  ).toHaveAttribute("href", `/runs/${fixture.source}`)
  const graph = page.getByRole("region", { name: "Run dependency graph" })
  await expect(graph).toHaveAttribute("data-version-id", fixture.version)
  await expect(
    graph.getByRole("button", { name: "build: Reused", exact: true })
  ).toBeVisible()
  await expect(
    graph.getByRole("button", { name: "deploy-eu: Reused", exact: true })
  ).toBeVisible()
  await expect(
    graph.getByRole("button", { name: "deploy-us: Succeeded", exact: true })
  ).toBeVisible()
  await expect(
    graph.getByRole("button", { name: "smoke: Succeeded", exact: true })
  ).toBeVisible()
  await graph
    .getByRole("button", { name: "build: Reused", exact: true })
    .click()
  await expect(
    page.getByRole("link", {
      name: "Original outputs and attempts for build",
      exact: true,
    })
  ).toHaveAttribute("href", `/runs/${fixture.source}`)
  await accessible(page)
  await page.getByRole("tab", { name: "Outputs", exact: true }).click()
  const outputs = page.getByRole("region", { name: "Task outputs" })
  const selection = outputs.getByLabel("Output attempt", { exact: true })
  await expect(selection.getByRole("option")).toHaveCount(8)
  await expect(
    selection.getByRole("option", { name: /^build\/|^deploy-eu\// })
  ).toHaveCount(0)
  await selection.selectOption(JSON.stringify(["smoke/api", 1]))
  const json = outputs.getByLabel("Attempt JSON outputs", { exact: true })
  await expect(json).toContainText("eu:ppe:9.8.7|us:ppe:9.8.7")
  expect(JSON.parse((await json.textContent())!)).toMatchObject({
    env: "ppe",
    version: "9.8.7",
    stage: "Smoke-api",
  })
  await page.getByRole("button", { name: "Dark theme" }).click()
  await page.setViewportSize({ width: 375, height: 812 })
  await accessible(page)
  await lineage
    .getByRole("link", {
      name: `build: original outputs in run ${fixture.source}`,
      exact: true,
    })
    .click()
  await expect(page).toHaveURL(new RegExp(`/runs/${fixture.source}$`))
  await expect(
    page
      .getByRole("region", { name: "Run controls" })
      .getByText("Failed", { exact: true })
  ).toBeVisible()
  await page.getByRole("tab", { name: "Outputs", exact: true }).click()
  await selection.selectOption(JSON.stringify(["build/compile", 1]))
  await expect(json).toContainText("ppe:9.8.7")
  expect(JSON.parse((await json.textContent())!)).toEqual(fixture.build)
  await selection.selectOption(JSON.stringify(["deploy-eu/deploy", 1]))
  await expect(json).toContainText("eu:ppe:9.8.7")
  expect(JSON.parse((await json.textContent())!)).toEqual(fixture.eu)
  await accessible(page)
  await expect(page.getByRole("alert")).toHaveCount(0)
})
