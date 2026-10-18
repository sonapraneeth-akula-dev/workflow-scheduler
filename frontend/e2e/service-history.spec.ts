import AxeBuilder from "@axe-core/playwright"
import { expect, test } from "@playwright/test"

test("navbar discovers team/type service views and restores format with two-week health and direct run actions", async ({
  page,
}) => {
  await page.goto("/")
  await page.getByRole("button", { name: "Service history menu" }).click()
  const menu = page.getByRole("menu", {
    name: "Service history teams",
    exact: true,
  })
  const teamItem = menu
    .locator('[role="menuitem"][aria-haspopup="menu"]')
    .first()
  await expect(teamItem).toBeVisible()
  const team = (await teamItem.textContent())!.trim()
  await teamItem.focus()
  await page.keyboard.press("ArrowRight")
  const types = page.getByRole("menu", {
    name: `${team} workflow types`,
    exact: true,
  })
  await expect(types).toBeVisible()
  const typeItem = types
    .getByRole("menuitem")
    .filter({
      hasNotText:
        /^(All workflow types|Loading types\.\.\.|No workflow types)$/,
    })
    .first()
  await expect(typeItem).toBeVisible()
  const type = (await typeItem.textContent())!.trim()
  await typeItem.click()
  await expect(
    page.getByRole("heading", { name: `${team} service history`, exact: true })
  ).toBeVisible()
  await expect(
    page.getByRole("combobox", { name: "Workflow type", exact: true })
  ).toHaveValue(type)
  await expect(page.getByText("Daily health: last 14 days (UTC)")).toBeVisible()
  await expect(page.getByLabel("Health window")).toHaveCount(0)
  const group = page.getByRole("region", { name: type, exact: true })
  await expect(group).toBeVisible()
  const card = group.locator("ul > li").first()
  await expect(card.getByRole("link", { name: /^History for / })).toBeVisible()
  await expect(
    card.getByRole("link", { name: /^View last run for / })
  ).toBeVisible()
  await expect(page.getByText("Daily health details")).toHaveCount(0)
  await expect(card.locator("[data-health-day]")).toHaveCount(14)
  await page.getByRole("radio", { name: "Table format" }).click()
  await expect(group.getByRole("table")).toBeVisible()
  expect(new URL(page.url()).searchParams.get("view")).toBe("table")
  await page.reload()
  await expect(group.getByRole("table")).toBeVisible()
  const row = group.getByRole("table").locator("tbody tr").first()
  await expect(row.locator("[data-health-day]")).toHaveCount(14)
  const lastRun = row.getByRole("link", { name: /^View last run for / })
  const lastRunPath = await lastRun.getAttribute("href")
  expect(lastRunPath).toMatch(/^\/runs\/[0-9a-f-]+$/)
  await lastRun.click()
  await expect.poll(() => new URL(page.url()).pathname).toBe(lastRunPath)
  await page.goBack()
  const history = group
    .getByRole("table")
    .locator("tbody tr")
    .first()
    .getByRole("link", { name: /^History for / })
  const historyPath = await history.getAttribute("href")
  expect(historyPath).toContain(
    `/t/${encodeURIComponent(team)}/${encodeURIComponent(type)}/w/`
  )
  await history.click()
  await expect.poll(() => new URL(page.url()).pathname).toBe(historyPath)
})

test("legacy service URLs use two weeks in both formats without dropping metadata grouping", async ({
  page,
}) => {
  await page.goto("/service-history")
  await page.locator('main a[href^="/t/"][href*="/services"]').first().click()
  await expect(
    page.getByRole("link", { name: /^History for / }).first()
  ).toBeVisible()
  const url = new URL(page.url())
  url.searchParams.set("groupBy", "metadata")
  url.searchParams.set("groupKey", "environment")
  for (const window of ["7d", "30d", "90d"]) {
    url.searchParams.set("window", window)
    url.searchParams.set("view", "cards")
    const health = page.waitForResponse((response) => {
      const request = new URL(response.url())
      return (
        request.pathname === "/api/v1/service-history" &&
        request.searchParams.get("window") === "14d" &&
        response.ok()
      )
    })
    await page.goto(url.toString())
    const response = await (await health).json()
    expect(response.window).toBe("14d")
    expect(response.items.length).toBeGreaterThan(0)
    for (const item of response.items) {
      expect(item.days).toHaveLength(14)
      expect(item.days[0].day).toBe(response.from)
      expect(Date.parse(response.to) - Date.parse(response.from)).toBe(
        14 * 86400000
      )
    }
    await expect(page.getByLabel("Container grouping")).toHaveValue(
      "metadata:environment"
    )
    const bars = page.locator('main [role="img"]')
    await expect(bars.first().locator("[data-health-day]")).toHaveCount(14)
    await page.getByRole("radio", { name: "Table format" }).click()
    await expect(
      page.locator("main tbody tr").first().locator("[data-health-day]")
    ).toHaveCount(14)
    expect(new URL(page.url()).searchParams.get("window")).toBe("14d")
  }
})

test("metadata container grouping survives format changes and reload without dropping workflows", async ({
  page,
}) => {
  await page.goto("/service-history")
  await page.locator('main a[href^="/t/"][href*="/services"]').first().click()
  const histories = page.getByRole("link", { name: /^History for / })
  await expect(histories.first()).toBeVisible()
  const count = await histories.count()
  const grouping = page.getByLabel("Container grouping")
  const value = await grouping
    .locator('option[value^="metadata:"]')
    .first()
    .getAttribute("value")
  if (!value)
    throw new Error("The registered sample metadata keys are missing.")
  await grouping.selectOption(value)
  await expect
    .poll(() => new URL(page.url()).searchParams.get("groupBy"))
    .toBe("metadata")
  expect(new URL(page.url()).searchParams.get("groupKey")).toBe(
    value.slice("metadata:".length)
  )
  await expect(histories).toHaveCount(count)
  await page.getByRole("radio", { name: "Table format" }).click()
  await expect(page.locator("main tbody tr")).toHaveCount(count)
  await page.reload()
  await expect(grouping).toHaveValue(value)
  await expect(page.locator("main tbody tr")).toHaveCount(count)
  const audit = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
    .analyze()
  expect(audit.violations).toEqual([])
  await grouping.selectOption("type")
  await expect
    .poll(() => new URL(page.url()).searchParams.get("groupKey"))
    .toBeNull()
  await expect(histories).toHaveCount(count)
})

for (const width of [320, 768, 2000]) {
  for (const colorScheme of ["light", "dark"] as const) {
    test(`${colorScheme} service cards and table remain accessible without page overflow at ${width}px`, async ({
      page,
    }, info) => {
      await page.emulateMedia({ colorScheme })
      await page.setViewportSize({ width, height: 1000 })
      await page.goto("/service-history")
      await page
        .locator('main a[href^="/t/"][href*="/services"]')
        .first()
        .click()
      await expect(page.getByRole("img").first()).toBeVisible()
      const bars = page.locator('main [role="img"]')
      await expect(bars.first().locator("[data-health-day]")).toHaveCount(14)
      for (const view of ["cards", "table"]) {
        if (view === "table")
          await page.getByRole("radio", { name: "Table format" }).click()
        await expect(
          page.getByRole("link", { name: /^History for / }).first()
        ).toBeVisible()
        expect(
          await page.evaluate(() => document.documentElement.scrollWidth)
        ).toBeLessThanOrEqual(width)
        const refresh = page.getByRole("button", {
          name: "Refresh service history",
        })
        await expect(refresh).toBeEnabled()
        await expect(refresh).toHaveCSS("opacity", "1")
        const audit = await new AxeBuilder({ page })
          .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
          .analyze()
        expect(audit.violations).toEqual([])
        await page.screenshot({
          path: info.outputPath(`services-${view}-${colorScheme}-${width}.png`),
          fullPage: true,
        })
      }
    })
  }
}
