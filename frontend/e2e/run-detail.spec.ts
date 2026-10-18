import { expect, test, type Page } from "@playwright/test"

async function openListedRun(page: Page) {
  await page.goto("/runs?status=Succeeded")
  const link = page.locator('main a[href^="/runs/"]').first()
  await expect(link).toBeVisible()
  const href = await link.getAttribute("href")
  expect(href).toMatch(/^\/runs\/[0-9a-f-]{36}$/)
  await link.click()
  await expect(
    page.getByRole("region", { name: "Run dependency graph", exact: true })
  ).toBeVisible()
  return href!
}

for (const width of [320, 768, 2000]) {
  test(`run summary keeps the graph near the top at ${width}px`, async ({
    page,
  }) => {
    await page.setViewportSize({ width, height: 1000 })
    await openListedRun(page)
    const summary = page.getByRole("region", { name: "Run summary" })
    await expect(summary).toBeVisible()
    await expect(summary).toContainText("Committed sequence:")
    await expect(summary).toContainText("Recorded run status:")
    await expect(
      summary.getByRole("link", { name: "Task Plan for this run" })
    ).toBeVisible()
    await expect(
      summary.getByRole("link", { name: "Workflow history", exact: true })
    ).toBeVisible()
    const bounds = await summary.boundingBox()
    expect(bounds!.height).toBeLessThan(width < 640 ? 320 : 180)
    const graph = page.getByRole("region", {
      name: "Run dependency graph",
      exact: true,
    })
    expect((await graph.boundingBox())!.y).toBeLessThan(width < 640 ? 650 : 450)
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth)
    ).toBeLessThanOrEqual(width)
    await page.locator("[data-graph-node]").first().click()
    const inspector = page.getByRole("region", { name: /^Selected node / })
    await expect(inspector).toBeVisible()
    const graphBounds = await graph.boundingBox()
    const inspectorBounds = await inspector.boundingBox()
    if (width >= 1280) {
      expect(inspectorBounds!.x).toBeGreaterThanOrEqual(
        graphBounds!.x + graphBounds!.width
      )
      expect(inspectorBounds!.y).toBe(graphBounds!.y)
    } else {
      expect(inspectorBounds!.y).toBeGreaterThanOrEqual(
        graphBounds!.y + graphBounds!.height
      )
    }
  })
}
