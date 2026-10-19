import AxeBuilder from "@axe-core/playwright"
import { expect, test } from "@playwright/test"

test("navbar discovers team/type service views and restores format with individual new-tab run actions", async ({
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
  const team = (await teamItem.getAttribute("data-team"))!
  const teamName = (await teamItem.textContent())!.trim()
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
  const typeName = (await typeItem.textContent())!.trim()
  const type = new URL(
    (await typeItem.getAttribute("href"))!,
    page.url()
  ).searchParams.get("type")!
  await typeItem.click()
  await expect(
    page.getByRole("heading", {
      name: `${teamName} Service History`,
      exact: true,
    })
  ).toBeVisible()
  await expect(
    page.getByRole("combobox", { name: "Workflow type", exact: true })
  ).toHaveValue(type)
  await expect(
    page.getByText("Run instances: latest 10 runs per workflow")
  ).toBeVisible()
  await expect(page.getByLabel("Health window")).toHaveCount(0)
  const group = page.getByRole("region", { name: typeName, exact: true })
  await expect(group).toBeVisible()
  const card = group.locator("ul > li").first()
  await expect(card.getByRole("link", { name: /^History for / })).toBeVisible()
  await expect(
    card.getByRole("link", { name: /^View last run for / })
  ).toBeVisible()
  await expect(page.getByText("Daily health details")).toHaveCount(0)
  const strip = card.locator("[data-run-history]")
  await expect(strip).toBeVisible()
  const run = strip.locator("[data-run-id]").first()
  await expect(run).toBeVisible()
  const runId = await run.getAttribute("data-run-id")
  const runStatus = await run.getAttribute("data-run-status")
  await expect(run).toHaveAttribute("href", `/runs/${runId}`)
  await expect(run).toHaveAttribute("target", "_blank")
  await expect(run).toHaveAttribute("rel", "noopener noreferrer")
  await expect(run).toHaveAccessibleName(
    new RegExp(`^${runStatus} run ${runId},`)
  )
  await run.focus()
  const popupPromise = page.waitForEvent("popup")
  await page.keyboard.press("Enter")
  const popup = await popupPromise
  await expect.poll(() => new URL(popup.url()).pathname).toBe(`/runs/${runId}`)
  await popup.close()
  await expect(group).toBeVisible()
  await page.getByRole("radio", { name: "Table format" }).click()
  await expect(group.getByRole("table")).toBeVisible()
  expect(new URL(page.url()).searchParams.get("view")).toBe("table")
  await page.reload()
  await expect(group.getByRole("table")).toBeVisible()
  const row = group.getByRole("table").locator("tbody tr").first()
  await expect(row.locator("[data-run-history]")).toBeVisible()
  await expect(row.locator("[data-run-id]").first()).toHaveAttribute(
    "target",
    "_blank"
  )
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

test("legacy service URLs use the personal run count without a date cutoff or dropping metadata grouping", async ({
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
        request.searchParams.get("runLimit") === "10" &&
        !request.searchParams.has("window") &&
        response.ok()
      )
    })
    await page.goto(url.toString())
    const response = await (await health).json()
    expect(response.runLimit).toBe(10)
    expect(response.items.length).toBeGreaterThan(0)
    for (const item of response.items) {
      expect(item.runs.length).toBeLessThanOrEqual(10)
      expect(item).not.toHaveProperty("days")
    }
    await expect(page.getByLabel("Container grouping")).toHaveValue(
      "metadata:environment"
    )
    const strips = page.locator("main [data-run-history]")
    await expect(strips).toHaveCount(response.items.length)
    const totalRuns = response.items.reduce(
      (sum: number, item: { runs: unknown[] }) => sum + item.runs.length,
      0
    )
    await expect(page.locator("main [data-run-id]")).toHaveCount(totalRuns)
    await page.getByRole("radio", { name: "Table format" }).click()
    await expect(page.locator("main tbody tr [data-run-history]")).toHaveCount(
      response.items.length
    )
    await expect(page.locator("main tbody tr [data-run-id]")).toHaveCount(
      totalRuns
    )
    expect(new URL(page.url()).searchParams.has("window")).toBe(false)
  }
})

test("personal Settings changes the recent run count and persists across reloads", async ({
  page,
}) => {
  await page.goto("/t/demo/services")
  await expect(
    page.getByText("Run instances: latest 10 runs per workflow")
  ).toBeVisible()
  await page.getByRole("button", { name: /^Account menu for / }).click()
  await page.getByRole("menuitem", { name: "Settings", exact: true }).click()
  const dialog = page.getByRole("dialog", { name: "Settings", exact: true })
  const count = dialog.getByRole("spinbutton", {
    name: "Recent runs per workflow",
  })
  await expect(count).toHaveValue("10")
  await count.fill("25")
  await dialog
    .getByRole("button", { name: "Save service history settings" })
    .click()
  await expect(dialog.getByRole("status")).toHaveText(
    "Personal run-count setting saved."
  )
  await dialog.getByRole("button", { name: "Close account dialog" }).click()
  await expect(
    page.getByText("Run instances: latest 25 runs per workflow")
  ).toBeVisible()
  await page.reload()
  await expect(
    page.getByText("Run instances: latest 25 runs per workflow")
  ).toBeVisible()
  const card = page
    .getByRole("link", {
      name: /^History for demo\/release\/five-minute-demo$/,
    })
    .locator("..")
    .locator("..")
  await expect(card.locator("[data-run-id]")).toHaveCount(25)
  await page.getByRole("radio", { name: "Table format" }).click()
  await expect(page.locator("main tbody tr [data-run-id]")).toHaveCount(25)
  await page.getByRole("button", { name: /^Account menu for / }).click()
  await page.getByRole("menuitem", { name: "Settings", exact: true }).click()
  await expect(count).toHaveValue("25")
  await count.fill("10")
  await dialog
    .getByRole("button", { name: "Save service history settings" })
    .click()
  await expect(dialog.getByRole("status")).toHaveText(
    "Personal run-count setting saved."
  )
})

test("configured capitalized names match source labels without changing routes or grouping keys", async ({
  page,
}) => {
  const teamsResponse = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === "/api/v1/teams" && response.ok()
  )
  await page.goto("/t/demo/services?groupBy=metadata&groupKey=component")
  const teams = await (await teamsResponse).json()
  const demo = teams.find((team: { team: string }) => team.team === "demo")
  expect(demo.presentation.names.demo).toBe("Demo")
  expect(demo.presentation.names["demo/release/five-minute-demo"]).toBe(
    "Five Minute Demo"
  )
  await expect(
    page.getByRole("heading", { name: "Demo Service History" })
  ).toBeVisible()
  await expect(
    page.getByLabel("Container grouping").locator("option:checked")
  ).toHaveText("Component")
  const group = page.getByRole("region", { name: "Dataflow Demo", exact: true })
  await expect(
    group.getByRole("heading", { name: "Five Minute Demo", exact: true })
  ).toBeVisible()
  const history = group.getByRole("link", {
    name: "History for demo/release/five-minute-demo",
  })
  await expect(history).toHaveAttribute(
    "href",
    "/t/demo/release/w/five-minute-demo/history"
  )
  await page.getByRole("radio", { name: "Table format" }).click()
  await expect(group.getByRole("rowheader")).toContainText("Five Minute Demo")
  expect(new URL(page.url()).searchParams.get("groupKey")).toBe("component")
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
      await expect(
        page.locator("main [data-run-history]").first()
      ).toBeVisible()
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
