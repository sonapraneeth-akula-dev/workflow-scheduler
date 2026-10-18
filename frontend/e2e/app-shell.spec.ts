import AxeBuilder from "@axe-core/playwright"
import { expect, test, type Page } from "@playwright/test"

async function expectSquareCorners(page: Page) {
  const rounded = await page.evaluate(() =>
    Array.from(document.querySelectorAll<HTMLElement>("body *"))
      .filter((element) => {
        const style = getComputedStyle(element)
        return (
          element.getClientRects().length > 0 &&
          style.visibility !== "hidden" &&
          [
            style.borderTopLeftRadius,
            style.borderTopRightRadius,
            style.borderBottomRightRadius,
            style.borderBottomLeftRadius,
          ].some((radius) => parseFloat(radius) !== 0)
        )
      })
      .map((element) => `${element.tagName}.${element.className}`)
  )
  expect(rounded).toEqual([])
}

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
  test(`main content uses the available width at ${width}px`, async ({
    page,
  }) => {
    await page.setViewportSize({ width, height: 1000 })
    await page.goto("/runs")
    const main = page.getByRole("main")
    await expect(main).toBeVisible()
    const content = await main.evaluate((element) => {
      const bounds = element.getBoundingClientRect()
      const style = getComputedStyle(element)
      return {
        width: bounds.width,
        left: parseFloat(style.paddingLeft),
        right: parseFloat(style.paddingRight),
      }
    })
    expect(content.width).toBe(width)
    expect(content.left).toBe(width < 640 ? 20 : 32)
    expect(content.right).toBe(content.left)
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth)
    ).toBeLessThanOrEqual(width)
  })

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

test("headings, body and run identifiers use their requested local font families", async ({
  page,
}) => {
  await page.goto("/runs")
  const heading = page.getByRole("heading").first()
  await expect(heading).toBeVisible()
  expect(
    await heading.evaluate((element) => getComputedStyle(element).fontFamily)
  ).toContain("Segoe UI Variable Display")
  expect(
    await page
      .locator("body")
      .evaluate((element) => getComputedStyle(element).fontFamily)
  ).toContain("Segoe UI Variable Text")
  const identifier = page.locator("main .font-mono").first()
  await expect(identifier).toBeVisible()
  expect(
    await identifier.evaluate((element) => getComputedStyle(element).fontFamily)
  ).toContain("Cascadia Code")
})

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
  for (const width of [320, 2000]) {
    test(`${colorScheme} surfaces remain square and accessible at ${width}px`, async ({
      page,
    }, testInfo) => {
      await page.emulateMedia({ colorScheme })
      await page.setViewportSize({ width, height: 1000 })
      await page.goto("/runs")
      await expect(page.locator('[data-slot="badge"]').first()).toBeVisible()
      await expectSquareCorners(page)
      const checkAccessibility = async (activeSurface?: string) => {
        const audit = new AxeBuilder({ page }).withTags([
          "wcag2a",
          "wcag2aa",
          "wcag21aa",
        ])
        if (activeSurface) audit.include(activeSurface)
        const result = await audit.analyze()
        expect(
          result.violations.map(({ id, nodes }) => ({
            id,
            nodes: nodes.map(({ target, failureSummary }) => ({
              target,
              failureSummary,
            })),
          }))
        ).toEqual([])
      }
      await checkAccessibility()
      await page.screenshot({
        path: testInfo.outputPath(`${colorScheme}-${width}.png`),
      })
      const account = page.getByRole("button", { name: /Account menu for / })
      await account.click()
      await expect(page.getByRole("menu")).toBeVisible()
      await expectSquareCorners(page)
      await checkAccessibility('[role="menu"]')
      await page
        .getByRole("menuitem", { name: "Settings", exact: true })
        .click()
      await expect(
        page.getByRole("dialog", { name: "Settings", exact: true })
      ).toBeVisible()
      await expectSquareCorners(page)
      await checkAccessibility('[role="dialog"]')
      await page.keyboard.press("Escape")
      await account.click()
      await page
        .getByRole("menuitem", { name: "Profile details", exact: true })
        .click()
      const profile = page.getByRole("dialog", {
        name: "Profile details",
        exact: true,
      })
      await expect(
        profile.getByText(
          "System administrator access includes all team and type permissions."
        )
      ).toBeVisible()
      await expectSquareCorners(page)
      await checkAccessibility('[role="dialog"]')
    })
  }

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
