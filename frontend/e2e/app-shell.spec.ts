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

test("account actions sit after the theme icon and expose verified local profile details", async ({
  page,
}) => {
  await page.setViewportSize({ width: 2000, height: 1000 })
  await page.goto("/runs")
  const account = page.getByRole("button", { name: /Account menu for / })
  await expect(account).toBeVisible()
  const theme = page.getByRole("button", { name: /^(Light|Dark) theme$/ })
  const accountBounds = await account.boundingBox()
  const themeBounds = await theme.boundingBox()
  expect(accountBounds!.x).toBeGreaterThan(themeBounds!.x + themeBounds!.width)
  expect(accountBounds!.x + accountBounds!.width).toBeGreaterThan(1950)
  await account.focus()
  await page.keyboard.press("Enter")
  const menu = page.getByRole("menu")
  await expect(menu.getByRole("separator")).toHaveCount(1)
  await expect(
    menu.getByRole("menuitem", { name: "Sign out", exact: true })
  ).toHaveAttribute("aria-disabled", "true")
  await menu
    .getByRole("menuitem", { name: "Profile details", exact: true })
    .click()
  const dialog = page.getByRole("dialog", {
    name: "Profile details",
    exact: true,
  })
  await expect(dialog).toBeVisible()
  await expect(
    dialog.getByText(
      "System administrator access includes all team and type permissions."
    )
  ).toBeVisible()
  await dialog.getByRole("button", { name: "Close account dialog" }).click()
  await expect(account).toBeFocused()
})

test("account settings changes the actual theme and offers system preference", async ({
  page,
}) => {
  await page.goto("/runs")
  await page.getByRole("button", { name: /Account menu for / }).click()
  await page.getByRole("menuitem", { name: "Settings", exact: true }).click()
  const dialog = page.getByRole("dialog", { name: "Settings", exact: true })
  await dialog.getByRole("radio", { name: "Dark", exact: true }).click()
  await expect(page.locator("html")).toHaveClass("dark")
  await dialog.getByRole("radio", { name: "System", exact: true }).click()
  expect(await page.evaluate(() => localStorage.getItem("kardia.theme"))).toBe(
    "system"
  )
  await page.keyboard.press("Escape")
  await expect(dialog).not.toBeVisible()
})

for (const colorScheme of ["light", "dark"] as const) {
  test(`icon toggle respects the ${colorScheme} system theme and persists the change`, async ({
    page,
  }) => {
    await page.emulateMedia({ colorScheme })
    await page.goto("/runs")
    const target = colorScheme === "dark" ? "Light theme" : "Dark theme"
    const button = page.getByRole("button", { name: target, exact: true })
    await expect(button).toBeVisible()
    await expect(button.locator("svg")).toHaveCount(1)
    expect(await button.innerText()).toBe("")
    await button.click()
    const next = colorScheme === "dark" ? "Dark theme" : "Light theme"
    await expect(
      page.getByRole("button", { name: next, exact: true })
    ).toBeVisible()
    await page.reload()
    await expect(
      page.getByRole("button", { name: next, exact: true })
    ).toBeVisible()
  })
}
