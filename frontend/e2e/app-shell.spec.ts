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

for (const width of [320, 768, 1440, 2000]) {
  test(`navigation remains centered at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 1000 })
    await page.goto("/runs")
    const navigation = page.getByRole("navigation", { name: "Main navigation" })
    await expect(navigation).toBeVisible()
    const bounds = await navigation.boundingBox()
    expect(bounds).not.toBeNull()
    expect(
      Math.abs(bounds!.x + bounds!.width / 2 - width / 2)
    ).toBeLessThanOrEqual(1)
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth)
    ).toBeLessThanOrEqual(width)
  })
}
