import { expect, test } from "@playwright/test"

test("brand stays at the padded left edge on wide screens", async ({
  page,
}) => {
  await page.setViewportSize({ width: 2000, height: 1000 })
  await page.goto("/runs")
  const brand = page.getByRole("link", { name: "Kardia home", exact: true })
  await expect(brand).toBeVisible()
  const bounds = await brand.boundingBox()
  expect(bounds).not.toBeNull()
  expect(bounds!.x).toBeGreaterThanOrEqual(24)
  expect(bounds!.x).toBeLessThanOrEqual(40)
  await expect(
    page.getByRole("navigation", { name: "Main navigation" })
  ).not.toContainText("Kardia")
})
