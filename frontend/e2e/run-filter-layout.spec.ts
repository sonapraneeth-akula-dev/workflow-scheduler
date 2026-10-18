import AxeBuilder from "@axe-core/playwright"
import { expect, test } from "@playwright/test"
import { openTeamRunList } from "./team-runs"

for (const width of [320, 768, 2000]) {
  test(`run filters leave results visible at ${width}px`, async ({
    page,
  }, info) => {
    await page.setViewportSize({ width, height: 1000 })
    await openTeamRunList(page)
    const form = page.getByRole("form", { name: "Run filters" })
    await expect(form.locator("details")).not.toHaveAttribute("open")
    expect((await form.boundingBox())!.height).toBeLessThan(
      width < 640 ? 310 : width < 1280 ? 190 : 130
    )
    const firstRun = page.locator("[data-run-row]").first()
    await expect(firstRun).toBeVisible()
    expect((await firstRun.boundingBox())!.y).toBeLessThan(
      width < 640 ? 900 : 700
    )
    await form.getByText("Advanced filters", { exact: true }).click()
    await form
      .getByLabel("Created from (inclusive RFC3339)")
      .fill("2020-01-01T00:00:00Z")
    await form
      .getByLabel("Metadata filters (JSON)")
      .fill('{"environment":"dev"}')
    await form.getByRole("button", { name: "Search runs" }).click()
    await page.reload()
    await expect(form.locator("details")).toHaveAttribute("open")
    await expect(
      form.getByLabel("Created from (inclusive RFC3339)")
    ).toHaveValue("2020-01-01T00:00:00Z")
    await expect(form.getByLabel("Metadata filters (JSON)")).toHaveValue(
      '{"environment":"dev"}'
    )
    await form.getByText(/^Advanced filters/).click()
    await expect(form.locator("details")).not.toHaveAttribute("open")
    await form.getByRole("button", { name: "Search runs" }).click()
    expect(new URL(page.url()).searchParams.get("from")).toBe(
      "2020-01-01T00:00:00Z"
    )
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth)
    ).toBeLessThanOrEqual(width)
    const accessibility = await new AxeBuilder({ page })
      .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
      .analyze()
    expect(accessibility.violations).toEqual([])
    await page.screenshot({
      path: info.outputPath(`compact-filters-${width}.png`),
      fullPage: true,
    })
  })
}
