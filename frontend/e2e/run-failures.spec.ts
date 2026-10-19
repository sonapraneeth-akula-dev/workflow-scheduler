import AxeBuilder from "@axe-core/playwright"
import { expect, test } from "@playwright/test"
import { z } from "zod"

const token = z.string().min(1).parse(process.env.WF_TIMELINE_TOKEN)
const runs = z
  .object({
    success: z.uuid(),
    retry: z.uuid(),
    service: z.uuid(),
    infra: z.uuid(),
  })
  .parse(JSON.parse(process.env.WF_FAILURE_RUNS ?? "{}"))

test.beforeEach(async ({ page }) => {
  await page.goto("/")
  await page.evaluate(async (jwt) => {
    const path = "/scripts/browser-auth.ts"
    const { hydrateBrowserSession }: typeof import("../scripts/browser-auth") =
      await import(path)
    await hydrateBrowserSession(jwt)
  }, token)
})

test("actual successful, recovered and failed runs retain every diagnostic and accessible full attempt messages", async ({
  page,
}) => {
  test.setTimeout(90_000)
  const writes: string[] = []
  page.on("request", (request) => {
    if (
      request.method() !== "GET" &&
      new URL(request.url()).pathname.startsWith("/api/")
    )
      writes.push(request.method())
  })
  for (const [scenario, id] of Object.entries(runs)) {
    let reads = 0
    const count = (request: import("@playwright/test").Request) => {
      if (new URL(request.url()).pathname === `/api/v1/runs/${id}/timeline`)
        reads++
    }
    page.on("request", count)
    await page.goto(`/runs/${id}`)
    const diagnostics = page.getByRole("region", {
      name: "Failure diagnostics",
    })
    await expect(diagnostics).toBeVisible()
    if (scenario === "success") {
      await expect(diagnostics).toContainText("No recorded execution failures.")
    } else {
      await expect(diagnostics.getByText(/All recorded failures/)).toBeVisible()
      const expected =
        scenario === "infra"
          ? "Infra / Storage (Failed)"
          : scenario === "service"
            ? "Service / Validation (Failed)"
            : "Infra / Network (Recovered x2)"
      await expect(diagnostics).toContainText(expected)
      await diagnostics.getByText(/All recorded failures/).click()
      await expect(
        diagnostics.getByText("optional-check/inspect - attempt 1")
      ).toBeVisible()
      await expect(diagnostics).toContainText(
        "Service / Diagnostics - Optional"
      )
      if (scenario === "retry") {
        await expect(
          diagnostics.getByText("fetch/request - attempt 1")
        ).toBeVisible()
        await expect(
          diagnostics.getByText("fetch/request - attempt 2")
        ).toBeVisible()
      }
    }
    expect(reads).toBe(1)
    await page.waitForTimeout(300)
    expect(reads).toBe(1)
    await page.getByRole("tab", { name: "Timeline", exact: true }).click()
    await expect(
      page.getByRole("columnheader", {
        name: "Failure type / category / message",
      })
    ).toBeVisible()
    if (scenario === "infra")
      await expect(page.getByRole("table")).toContainText(
        "blob storage unavailable"
      )
    if (scenario === "retry")
      await expect(page.getByRole("table")).toContainText(
        "Infra / Network - Recovered"
      )
    page.off("request", count)
  }
  for (const dark of [false, true]) {
    await page.setViewportSize(
      dark ? { width: 375, height: 812 } : { width: 1440, height: 1080 }
    )
    await page.evaluate(
      (dark) => document.documentElement.classList.toggle("dark", dark),
      dark
    )
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
  expect(writes).toEqual([])
})

test("run cards, workflow table, workflow history and both service formats expose mapped type, category and message", async ({
  page,
}) => {
  test.setTimeout(90_000)
  await page.goto("/t/demo/runs")
  const card = page.locator(`[data-run-row="${runs.infra}"]`)
  await expect(card).toContainText("Infra / Storage (Failed)")
  await expect(card).toContainText("blob storage unavailable")
  const recovered = page.locator(`[data-run-row="${runs.retry}"]`)
  await expect(recovered).toContainText("Infra / Network (Recovered x2)")
  await expect(recovered).toContainText("Succeeded")

  await page.goto("/t/demo/metrics")
  const workflow = page
    .getByRole("row")
    .filter({ has: page.locator(`a[href="/runs/${runs.infra}"]`) })
  await expect(workflow).toContainText("Infra / Storage (Failed)")
  await expect(workflow).toContainText("blob storage unavailable")

  await page.goto("/t/demo/metrics/w/failure-infra/history")
  const history = page.getByRole("table")
  await expect(history).toContainText("Infra / Storage (Failed)")
  await expect(history).toContainText("blob storage unavailable")

  await page.goto("/t/demo/services?type=metrics")
  for (const format of ["Card format", "Table format"]) {
    await page.getByRole("radio", { name: format }).click()
    const region = page.getByRole("region", { name: /^Metrics$/i })
    await expect(region).toContainText("Infra / Storage (Failed)")
    await expect(region).toContainText("Service / Validation (Failed)")
    await expect(region).toContainText("Infra / Network (Recovered x2)")
    const icon = region.locator(`[data-run-id="${runs.infra}"]`)
    await expect(icon).toHaveAccessibleName(
      /Infra \/ Storage \(Failed\).*blob storage unavailable/
    )
    await region
      .getByText("Failure details for run instances", { exact: true })
      .first()
      .click()
    await expect(
      region
        .locator("[data-run-history]")
        .first()
        .locator("[data-failure-summary]")
    ).toBeVisible()
  }
})
