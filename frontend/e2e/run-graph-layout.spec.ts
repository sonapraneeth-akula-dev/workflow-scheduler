import AxeBuilder from "@axe-core/playwright"
import { expect, test } from "@playwright/test"
import { z } from "zod"

const runId = z.uuid().parse(process.env.WF_GRAPH_LAYOUT_RUN)

test("graph and node details share desktop space and stack on smaller screens", async ({
  page,
}) => {
  test.setTimeout(120_000)
  await page.setViewportSize({ width: 1920, height: 1080 })
  await page.goto(`/runs/${runId}`)
  const graph = page.getByRole("region", {
    name: "Run dependency graph",
    exact: true,
  })
  const list = page.getByText("Node list", { exact: true }).locator("..")
  const table = page.getByRole("table", { name: "Run nodes" })
  await expect(table).toBeHidden()
  await list.locator("summary").click()
  await expect(table).toBeVisible()
  const nodes = graph.locator("[data-graph-node]")
  expect(await nodes.count()).toBeGreaterThan(0)
  const first = nodes.first()
  const key = await first.getAttribute("data-graph-node")
  await first.click()
  const inspector = page.getByRole("region", {
    name: `Selected node ${key}`,
    exact: true,
  })
  await expect(inspector).toBeVisible()
  const position = await first.getAttribute("style")
  for (const width of [1920, 1440, 1280, 1024, 768, 375, 320]) {
    await page.setViewportSize({ width, height: 1080 })
    const boxes = await Promise.all([
      graph.boundingBox(),
      list.boundingBox(),
      inspector.boundingBox(),
    ])
    const [graphBox, listBox, inspectorBox] = boxes
    expect(graphBox).not.toBeNull()
    expect(listBox).not.toBeNull()
    expect(inspectorBox).not.toBeNull()
    if (!graphBox || !listBox || !inspectorBox)
      throw new Error("Responsive graph panels are missing.")
    if (width >= 1280) {
      expect(graphBox.x + graphBox.width).toBeLessThan(listBox.x)
      expect(Math.abs(graphBox.y - listBox.y)).toBeLessThanOrEqual(1)
      expect(Math.abs(graphBox.width - listBox.width)).toBeLessThanOrEqual(1)
    } else {
      expect(listBox.y).toBeGreaterThanOrEqual(graphBox.y + graphBox.height)
      expect(Math.abs(graphBox.x - listBox.x)).toBeLessThanOrEqual(1)
    }
    expect(inspectorBox.y).toBeGreaterThanOrEqual(listBox.y + listBox.height)
    expect(Math.abs(inspectorBox.x - listBox.x)).toBeLessThanOrEqual(1)
    expect(Math.abs(inspectorBox.width - listBox.width)).toBeLessThanOrEqual(1)
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth)
    ).toBeLessThanOrEqual(width)
    await expect(first).toHaveAttribute("style", position!)
    await expect(first).toHaveAttribute("aria-pressed", "true")
    if (width === 1920 || width === 375)
      for (const dark of [false, true]) {
        await page.evaluate(
          (enabled) =>
            document.documentElement.classList.toggle("dark", enabled),
          dark
        )
        const result = await new AxeBuilder({ page })
          .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
          .analyze()
        expect(result.violations).toEqual([])
        await page.screenshot({
          path: test
            .info()
            .outputPath(`graph-${width}-${dark ? "dark" : "light"}.png`),
          fullPage: true,
        })
      }
  }
  await first.focus()
  await page.keyboard.press("Tab")
  await expect(list.locator("summary")).toBeFocused()
  await page.keyboard.press("Enter")
  await expect(table).toBeHidden()
  await expect(inspector).toBeVisible()
  await page.keyboard.press("Enter")
  await expect(table).toBeVisible()
})
