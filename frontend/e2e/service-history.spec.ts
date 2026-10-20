import AxeBuilder from "@axe-core/playwright"
import { expect, test } from "@playwright/test"

test("outcome summary uses readable severity colors and separate Infra and Service category rows", async ({
  page,
}) => {
  test.setTimeout(90_000)
  const team = process.env.WF_SUMMARY_TEAM ?? "ui-tests"
  for (const colorScheme of ["light", "dark"] as const) {
    await page.emulateMedia({ colorScheme })
    for (const width of [390, 1440]) {
      await page.setViewportSize({ width, height: 1000 })
      await page.goto(`/t/${encodeURIComponent(team)}/services`)
      const disclosure = page.locator("details").filter({
        has: page.locator("summary", { hasText: "Run outcome summary" }),
      })
      await expect(disclosure).toHaveAttribute("open", "")
      await expect(disclosure.getByText(/unique runs? across/)).toBeVisible()
      const infra = disclosure.getByRole("region", {
        name: "Infra failure breakdown",
      })
      const service = disclosure.getByRole("region", {
        name: "Service failure breakdown",
      })
      await expect(infra).toBeVisible()
      await expect(service).toBeVisible()
      await expect(
        infra.locator("li").filter({ hasText: /^Service \// })
      ).toHaveCount(0)
      await expect(
        service.locator("li").filter({ hasText: /^Infra \// })
      ).toHaveCount(0)
      const infraBox = await infra.boundingBox()
      const serviceBox = await service.boundingBox()
      expect(infraBox).not.toBeNull()
      expect(serviceBox).not.toBeNull()
      expect(serviceBox!.y).toBeGreaterThanOrEqual(
        infraBox!.y + infraBox!.height
      )
      for (const label of ["Failed runs", "Timed out runs", "Unknown cause"]) {
        const tile = disclosure.getByText(label, { exact: true }).locator("..")
        const count = Number(await tile.locator("dd").textContent())
        expect(Number.isInteger(count)).toBe(true)
        if (count)
          await expect(tile).toHaveClass(
            label === "Failed runs" ? /bg-destructive\/10/ : /bg-warning\/10/
          )
      }
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth)
      ).toBeLessThanOrEqual(width)
      const audit = await new AxeBuilder({ page })
        .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
        .analyze()
      expect(audit.violations).toEqual([])
    }
  }
})

test("Regression contains distinct Linear and Logistic workflows with their own training and full-test cards", async ({
  page,
}) => {
  test.skip(
    !process.env.WF_REGRESSION_SAMPLE,
    "Requires the published regression samples."
  )
  test.setTimeout(90_000)
  for (const colorScheme of ["light", "dark"] as const) {
    await page.emulateMedia({ colorScheme })
    for (const width of [390, 1999]) {
      await page.setViewportSize({ width, height: 1100 })
      await page.goto("/t/data-science/services?type=regression")
      const regression = page.getByRole("region", {
        name: "Regression",
        exact: true,
      })
      await expect(regression).toBeVisible()
      await expect(
        regression.getByText("4 loaded cards", { exact: true })
      ).toBeVisible()
      for (const algorithm of ["linear", "logistic"]) {
        const container = regression.locator(
          `[data-workflow-container="data-science/regression/${algorithm}-regression"]`
        )
        await expect(container).toBeVisible()
        for (const name of ["Training & validation", "Full test"]) {
          const card = container
            .getByRole("heading", { name, exact: true, level: 4 })
            .locator("..")
            .locator("..")
          await expect(card).toBeVisible()
          const href = await card
            .getByRole("link", { name: /^History for / })
            .getAttribute("href")
          const includeManual = new URL(href!, page.url()).searchParams.get(
            "includeManual"
          )
          expect(JSON.parse(includeManual!)).toBe("false")
        }
        await expect(container.locator(":scope > ul > li")).toHaveCount(2)
      }
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth)
      ).toBeLessThanOrEqual(width)
      const audit = await new AxeBuilder({ page })
        .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
        .analyze()
      expect(audit.violations).toEqual([])
    }
  }
  await page.getByRole("radio", { name: "Table format" }).click()
  const table = page
    .getByRole("region", { name: "Regression", exact: true })
    .getByRole("table")
  await expect(table.getByRole("rowheader")).toHaveCount(4)
  await expect(
    table.getByRole("rowheader").filter({ hasText: "logistic-regression" })
  ).toHaveCount(2)
  await expect(
    table.getByRole("rowheader").filter({ hasText: "linear-regression" })
  ).toHaveCount(2)
})

test("navbar discovers team/type service views and restores format with individual new-tab run actions", async ({
  page,
}) => {
  await page.goto("/")
  await page.getByRole("button", { name: "Service history menu" }).click()
  const menu = page.getByRole("menu", {
    name: "Service history teams",
    exact: true,
  })
  const teamItem = menu.locator(
    '[role="menuitem"][aria-haspopup="menu"][data-team="ui-tests"]'
  )
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
  const card = group
    .locator("ul > li")
    .filter({ has: page.getByRole("link", { name: /^View last run for / }) })
    .first()
  await expect(card.getByRole("link", { name: /^History for / })).toBeVisible()
  await expect(
    card.getByRole("link", { name: /^View last run for / })
  ).toBeVisible()
  for (const link of [
    card.getByRole("link", { name: /^History for / }),
    card.getByRole("link", { name: /^View last run for / }),
  ]) {
    await expect(link).toHaveAttribute("target", "_blank")
    await expect(link).toHaveAttribute("rel", "noopener noreferrer")
    await expect(link).toHaveAccessibleDescription("Opens in a new tab.")
    await expect(link.locator('svg[data-icon="inline-end"]')).toBeVisible()
  }
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
  const row = group
    .getByRole("table")
    .locator("tbody tr")
    .filter({ has: page.getByRole("link", { name: /^View last run for / }) })
    .first()
  await expect(row.locator("[data-run-history]")).toBeVisible()
  await expect(row.locator("[data-run-id]").first()).toHaveAttribute(
    "target",
    "_blank"
  )
  const lastRun = row.getByRole("link", { name: /^View last run for / })
  const lastRunPath = await lastRun.getAttribute("href")
  expect(lastRunPath).toMatch(/^\/runs\/[0-9a-f-]+$/)
  const serviceUrl = page.url()
  const lastRunPopup = page.waitForEvent("popup")
  await lastRun.click()
  const lastRunPage = await lastRunPopup
  await expect.poll(() => new URL(lastRunPage.url()).pathname).toBe(lastRunPath)
  expect(await lastRunPage.evaluate(() => window.opener === null)).toBe(true)
  await expect(page).toHaveURL(serviceUrl)
  await lastRunPage.close()
  const history = row.getByRole("link", { name: /^History for / })
  const historyPath = await history.getAttribute("href")
  expect(historyPath).toContain(
    `/t/${encodeURIComponent(team)}/${encodeURIComponent(type)}/w/`
  )
  const historyPopup = page.waitForEvent("popup")
  await history.click()
  const historyPage = await historyPopup
  await expect.poll(() => new URL(historyPage.url()).pathname).toBe(historyPath)
  await expect(
    historyPage.getByRole("heading", { name: "Workflow history", exact: true })
  ).toBeVisible()
  await expect(page).toHaveURL(serviceUrl)
  await historyPage.close()
  const navbarRuns = page
    .getByRole("navigation", { name: "Main navigation", exact: true })
    .getByRole("link", { name: "Runs", exact: true })
  await expect(navbarRuns).not.toHaveAttribute("target", "_blank")
  await expect(navbarRuns.locator('svg[data-icon="inline-end"]')).toHaveCount(0)
  await navbarRuns.click()
  await expect.poll(() => new URL(page.url()).pathname).toBe("/runs")
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

test("recent-run count and success rate stay adjacent in compact cards and table columns", async ({
  page,
}) => {
  await page.goto("/t/report-tests/services")
  const strip = page.locator("main [data-run-history]").first()
  await expect(strip.locator("[data-run-id]")).toHaveCount(1)
  for (const width of [1280, 2000]) {
    await page.setViewportSize({ width, height: 1000 })
    for (const view of ["cards", "table"]) {
      await page
        .getByRole("radio", {
          name: view === "cards" ? "Card format" : "Table format",
        })
        .click()
      const layout = await strip.evaluate((element) => {
        const summary = element.lastElementChild!
        const count = summary.firstElementChild!.getBoundingClientRect()
        const success = summary.lastElementChild!.getBoundingClientRect()
        const column = element.closest("td")?.getBoundingClientRect()
        return {
          gap: success.left - count.right,
          lineOffset: Math.abs(success.top - count.top),
          columnWidth: column?.width,
        }
      })
      expect(layout.lineOffset).toBeLessThanOrEqual(1)
      expect(layout.gap).toBeGreaterThanOrEqual(8)
      expect(layout.gap).toBeLessThanOrEqual(13)
      if (view === "table") expect(layout.columnWidth).toBeLessThanOrEqual(301)
    }
  }
})

test("personal run settings cap at 30 and additional icons wrap without reserving width", async ({
  page,
}) => {
  await page.route("**/api/v1/service-history?**", async (route) => {
    const response = await route.fetch()
    const data: {
      runLimit: number
      items: { runs: { id: string; createdAt: string }[] }[]
    } = await response.json()
    expect(data.runLimit).toBeLessThanOrEqual(30)
    const entry = data.items[0]
    const original = entry?.runs[0]
    if (!entry || !original) throw new Error("The seeded run is missing.")
    entry.runs = Array.from({ length: data.runLimit }, (_, index) => ({
      ...original,
      id:
        index === 0
          ? original.id
          : `00000000-0000-4000-8000-${String(index).padStart(12, "0")}`,
      createdAt:
        index === 0
          ? original.createdAt
          : new Date(
              new Date(original.createdAt).getTime() - index * 1000
            ).toISOString(),
    }))
    await route.fulfill({ response, json: data })
  })
  await page.setViewportSize({ width: 1280, height: 1000 })
  await page.goto("/t/report-tests/services")
  const strip = page.locator("main [data-run-history]").first()
  await expect(strip.locator("[data-run-id]")).toHaveCount(10)
  const initial = (await strip.boundingBox())!
  const card = page.locator("main ul > li").first()
  const initialCard = (await card.boundingBox())!
  await page.getByRole("button", { name: /^Account menu for / }).click()
  await page.getByRole("menuitem", { name: "Settings", exact: true }).click()
  const dialog = page.getByRole("dialog", { name: "Settings", exact: true })
  const count = dialog.getByRole("spinbutton", {
    name: "Recent runs per workflow",
  })
  await expect(count).toHaveAttribute("max", "30")
  await count.fill("31")
  const save = dialog.getByRole("button", {
    name: "Save service history settings",
  })
  await save.click()
  expect(
    await count.evaluate(
      (element) =>
        element instanceof HTMLInputElement && element.validity.rangeOverflow
    )
  ).toBe(true)
  await expect(dialog.getByRole("status")).toHaveCount(0)
  await count.fill("30")
  await save.click()
  await expect(dialog.getByRole("status")).toHaveText(
    "Personal run-count setting saved."
  )
  await dialog.getByRole("button", { name: "Close account dialog" }).click()
  await expect(strip.locator("[data-run-id]")).toHaveCount(30)
  const expanded = (await strip.boundingBox())!
  expect(expanded.width).toBeLessThanOrEqual(initial.width + 1)
  expect(expanded.height).toBeGreaterThanOrEqual(initial.height + 55)
  expect((await card.boundingBox())!.height).toBeGreaterThanOrEqual(
    initialCard.height + 55
  )
  for (const view of ["cards", "table"]) {
    if (view === "table")
      await page.getByRole("radio", { name: "Table format" }).click()
    const layout = await strip.evaluate((element) => {
      const icons = Array.from(
        element.querySelectorAll("[data-run-id]"),
        (icon) => icon.getBoundingClientRect()
      )
      return {
        rows: new Set(icons.map((icon) => icon.top)).size,
        maxSize: Math.max(...icons.map((icon) => icon.width)),
        columnWidth: element.closest("td")?.getBoundingClientRect().width,
      }
    })
    expect(layout.rows).toBe(3)
    expect(layout.maxSize).toBe(24)
    if (view === "table") expect(layout.columnWidth).toBeLessThanOrEqual(301)
  }
  await page.getByRole("radio", { name: "Card format" }).click()
  await page.setViewportSize({ width: 320, height: 1000 })
  await page.reload()
  await expect(strip.locator("[data-run-id]")).toHaveCount(30)
  expect(
    await page.evaluate(() => document.documentElement.scrollWidth)
  ).toBeLessThanOrEqual(320)
  const audit = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
    .analyze()
  expect(audit.violations).toEqual([])
})

for (const width of [320, 768, 2000]) {
  for (const colorScheme of ["light", "dark"] as const) {
    test(`${colorScheme} service cards and table remain accessible without page overflow at ${width}px`, async ({
      page,
    }, info) => {
      await page.emulateMedia({ colorScheme })
      await page.setViewportSize({ width, height: 1000 })
      await page.goto("/t/report-tests/services")
      await expect(
        page.locator("main [data-run-history]").first()
      ).toBeVisible()
      for (const view of ["cards", "table"]) {
        if (view === "table")
          await page.getByRole("radio", { name: "Table format" }).click()
        if (view === "cards") {
          const timestamp = page.locator("main li > p > time").first()
          await expect(timestamp).toBeVisible()
          const layout = await timestamp.evaluate((element) => {
            const label = element.previousElementSibling
            if (!label) throw new Error("Last-run label is missing.")
            return {
              offset: Math.abs(
                label.getBoundingClientRect().top -
                  element.getClientRects()[0]!.top
              ),
              lines: element.getClientRects().length,
            }
          })
          expect(layout.offset).toBeLessThanOrEqual(1)
          if (width === 2000) expect(layout.lines).toBe(1)
        }
        await expect(
          page.getByRole("link", { name: /^History for / }).first()
        ).toBeVisible()
        const summary = await page
          .locator("main [data-run-history]")
          .first()
          .evaluate((element) => {
            const bounds = element.getBoundingClientRect()
            const summary = element.lastElementChild!
            return Array.from(summary.children).every((child) => {
              const rect = child.getBoundingClientRect()
              return (
                rect.left >= bounds.left - 1 &&
                rect.right <= bounds.right + 1 &&
                rect.bottom <= bounds.bottom + 1
              )
            })
          })
        expect(summary).toBe(true)
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
