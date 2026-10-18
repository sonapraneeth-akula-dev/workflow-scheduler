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

for (const colorScheme of ["light", "dark"] as const) {
  test(`${colorScheme} dependency arrows stay outside every node and stop before the target border`, async ({
    page,
  }, testInfo) => {
    await page.emulateMedia({ colorScheme })
    await page.setViewportSize({ width: 2000, height: 1000 })
    await openListedRun(page)
    const geometry = await page
      .locator("[data-graph-edge]")
      .evaluateAll((paths) =>
        paths.map((element) => {
          if (!(element instanceof SVGPathElement))
            throw new Error("Expected SVG dependency path.")
          const canvas = element.ownerSVGElement!.getBoundingClientRect()
          const cards = Array.from(
            document.querySelectorAll<HTMLElement>("[data-graph-node]")
          ).map((card) => {
            const box = card.getBoundingClientRect()
            return {
              key: card.dataset.graphNode,
              left: box.left - canvas.left,
              right: box.right - canvas.left,
              top: box.top - canvas.top,
              bottom: box.bottom - canvas.top,
            }
          })
          const [from, to] = element.getAttribute("data-graph-edge")!.split("/")
          const source = cards.find((card) => card.key === from)!
          const target = cards.find((card) => card.key === to)!
          const length = element.getTotalLength()
          const start = element.getPointAtLength(0)
          const end = element.getPointAtLength(length)
          const intersections: string[] = []
          for (let offset = 1; offset < length; offset += 2) {
            const point = element.getPointAtLength(offset)
            for (const card of cards)
              if (
                point.x > card.left + 1 &&
                point.x < card.right - 1 &&
                point.y > card.top + 1 &&
                point.y < card.bottom - 1
              )
                intersections.push(card.key!)
          }
          return {
            edge: `${from}/${to}`,
            sourceGap: start.x - source.right,
            targetGap: target.left - end.x,
            sourceInside: start.y > source.top && start.y < source.bottom,
            targetInside: end.y > target.top && end.y < target.bottom,
            intersections,
            stroke: parseFloat(getComputedStyle(element).strokeWidth),
          }
        })
      )
    expect(geometry.length).toBeGreaterThan(0)
    for (const edge of geometry) {
      expect(edge.intersections, edge.edge).toEqual([])
      expect(edge.sourceGap, edge.edge).toBeCloseTo(0)
      expect(edge.targetGap, edge.edge).toBeCloseTo(4)
      expect(edge.sourceInside && edge.targetInside, edge.edge).toBe(true)
      expect(edge.stroke).toBeGreaterThanOrEqual(2)
    }
    await page
      .getByRole("region", {
        name: "Run dependency graph",
        exact: true,
      })
      .screenshot({
        path: testInfo.outputPath(`arrows-${colorScheme}.png`),
      })
  })
}
