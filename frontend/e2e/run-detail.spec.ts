import AxeBuilder from "@axe-core/playwright"
import { expect, test, type Page } from "@playwright/test"
import { openTeamRunList } from "./team-runs"

test.use({
  launchOptions: {
    // Preserve native Firefox reload history (microsoft/playwright#22640).
    firefoxUserPrefs: { "fission.webContentIsolationStrategy": 1 },
  },
})

async function openListedRun(page: Page) {
  await openTeamRunList(page)
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

test("Back returns to the exact filtered run listing, including after reload", async ({
  page,
}) => {
  await openListedRun(page)
  await page.reload()
  await page.getByRole("button", { name: "Back to previous page" }).click()
  await expect(page).toHaveURL(/\/t\/[^/]+\/runs\?status=Succeeded$/)
  await expect(
    page.getByRole("heading", { name: / runs$/, exact: true })
  ).toBeVisible()
})

test("Back returns to the workflow history that opened this run", async ({
  page,
}) => {
  const runPath = await openListedRun(page)
  await page
    .getByRole("link", { name: "Workflow history", exact: true })
    .click()
  const origin = page.url()
  await page.locator(`main a[href="${runPath}"]`).first().click()
  await page.getByRole("button", { name: "Back to previous page" }).click()
  await expect(page).toHaveURL(origin)
})

test("direct-entry Back replaces the run with Runs instead of leaving the app or creating a loop", async ({
  page,
  browser,
}) => {
  const runPath = await openListedRun(page)
  const context = await browser.newContext({
    baseURL: new URL(page.url()).origin,
  })
  try {
    const direct = await context.newPage()
    await direct.goto("data:text/html,External%20entry")
    await direct.goto(runPath)
    await direct.getByRole("button", { name: "Back to runs" }).click()
    await expect(direct).toHaveURL(/\/runs$/)
    expect(await direct.evaluate(() => history.state.__TSR_index)).toBe(0)
    await expect(
      direct.getByRole("heading", { name: "Runs", exact: true })
    ).toBeVisible()
  } finally {
    await context.close()
  }
})

for (const colorScheme of ["light", "dark"] as const) {
  for (const width of [320, 2000]) {
    test(`${colorScheme} run tabs clearly select one square view without wrapping at ${width}px`, async ({
      page,
    }, testInfo) => {
      await page.emulateMedia({ colorScheme })
      await page.setViewportSize({ width, height: 1000 })
      await openListedRun(page)
      const historyIndex = await page.evaluate(() => history.state.__TSR_index)
      const tabs = page.getByRole("tablist", { name: "Run views" })
      expect((await tabs.boundingBox())!.height).toBeLessThanOrEqual(70)
      const rows = await tabs
        .getByRole("tab")
        .evaluateAll((elements) =>
          elements.map((element) => element.getBoundingClientRect().y)
        )
      expect(new Set(rows).size).toBe(1)
      const originalNode = await page
        .locator("[data-graph-node]")
        .first()
        .elementHandle()
      for (const name of [
        "Timeline",
        "Metrics",
        "Logs",
        "Outputs",
        "Reports",
        "System Events",
        "Graph",
      ]) {
        const tab = tabs.getByRole("tab", { name, exact: true })
        await tab.click()
        await expect(tabs.getByRole("tab", { selected: true })).toHaveText(name)
        await expect(page.getByRole("tabpanel")).toHaveAttribute(
          "aria-labelledby",
          (await tab.getAttribute("id"))!
        )
        await expect(tab).toHaveCSS("border-bottom-width", "2px")
        await expect(tab).toHaveCSS("font-weight", "600")
        await expect(tab).toHaveCSS("border-top-left-radius", "0px")
        await expect(tab).toHaveCSS("border-bottom-right-radius", "0px")
      }
      expect(await originalNode!.evaluate((node) => node.isConnected)).toBe(
        true
      )
      const graphTab = tabs.getByRole("tab", { name: "Graph", exact: true })
      const inactive = tabs.getByRole("tab", { name: "Timeline", exact: true })
      expect(
        await graphTab.evaluate(
          (element) => getComputedStyle(element).backgroundColor
        )
      ).not.toBe(
        await inactive.evaluate(
          (element) => getComputedStyle(element).backgroundColor
        )
      )
      await graphTab.focus()
      await page.keyboard.press("Tab")
      await page.keyboard.press("Shift+Tab")
      await expect(graphTab).toBeFocused()
      await page.keyboard.press("ArrowRight")
      await expect(inactive).toBeFocused()
      await expect(inactive).toHaveAttribute("aria-selected", "true")
      await page.keyboard.press("End")
      await expect(
        tabs.getByRole("tab", { name: "System Events", exact: true })
      ).toBeFocused()
      await page.keyboard.press("Home")
      await expect(graphTab).toBeFocused()
      await expect(graphTab).toHaveAttribute("aria-selected", "true")
      await expect(graphTab).toHaveCSS("outline-width", "2px")
      await page.locator("[data-graph-node]").first().click()
      await expect(
        page.getByRole("region", { name: /^Selected node / })
      ).toBeVisible()
      const rounded = await page.locator("main").evaluate((main) =>
        Array.from(main.querySelectorAll("*"))
          .filter((element) => {
            const style = getComputedStyle(element)
            return (
              element.getClientRects().length &&
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
      const audit = await new AxeBuilder({ page })
        .withTags(["wcag2a", "wcag2aa", "wcag21aa"])
        .analyze()
      expect(
        audit.violations.map(({ id, nodes }) => ({
          id,
          nodes: nodes.map(({ target, failureSummary }) => ({
            target,
            failureSummary,
          })),
        }))
      ).toEqual([])
      expect(await page.evaluate(() => history.state.__TSR_index)).toBe(
        historyIndex
      )
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth)
      ).toBeLessThanOrEqual(width)
      await page.evaluate(() => window.scrollTo(0, 0))
      await page.screenshot({
        path: testInfo.outputPath(`run-detail-${colorScheme}-${width}.png`),
        fullPage: true,
      })
      await page.getByRole("button", { name: "Back to previous page" }).click()
      await expect(page).toHaveURL(/\/t\/[^/]+\/runs\?status=Succeeded$/)
    })
  }

  test(`${colorScheme} node table has striped rows, a bold tinted header and keyboard selection without page overflow`, async ({
    page,
  }, testInfo) => {
    await page.emulateMedia({ colorScheme })
    await page.setViewportSize({ width: 2000, height: 1000 })
    await openListedRun(page)
    const nodes = await page.locator("[data-graph-node]").evaluateAll((cards) =>
      cards.map((card) => ({
        key: card.getAttribute("data-graph-node")!,
        status: card.querySelector('[data-slot="badge"]')!.textContent!,
      }))
    )
    await page.getByText("Node list", { exact: true }).click()
    const table = page.getByRole("table", { name: "Run nodes" })
    await expect(table).toBeVisible()
    await expect(table.getByRole("columnheader")).toHaveText([
      "Node",
      "Status",
      "Dependencies",
    ])
    await expect(table.getByRole("row")).toHaveCount(nodes.length + 1)
    await expect(table.getByRole("columnheader").first()).toHaveCSS(
      "font-weight",
      "700"
    )
    const rowColors = await table
      .locator("tbody tr")
      .evaluateAll((rows) =>
        rows.map((row) => getComputedStyle(row).backgroundColor)
      )
    expect(rowColors[0]).not.toBe(rowColors[1])
    const headerColor = await table
      .locator("thead")
      .evaluate((header) => getComputedStyle(header).backgroundColor)
    expect(headerColor).not.toBe(rowColors[0])
    for (const [index, node] of nodes.entries()) {
      const row = table.locator("tbody tr").nth(index)
      await expect(row.getByRole("button")).toHaveAccessibleName(
        `Inspect node ${node.key}`
      )
      await expect(row.locator('[data-slot="badge"]')).toHaveText(node.status)
      await expect(row.getByRole("cell").last()).not.toBeEmpty()
    }
    const last = table.getByRole("button").last()
    await last.focus()
    await page.keyboard.press("Enter")
    await expect(last).toHaveAttribute("aria-pressed", "true")
    await expect(
      page.getByRole("region", { name: `Selected node ${nodes.at(-1)!.key}` })
    ).toBeVisible()
    for (const width of [2000, 320]) {
      await page.setViewportSize({ width, height: 1000 })
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth)
      ).toBeLessThanOrEqual(width)
      await page
        .getByRole("region", { name: "Scrollable node table" })
        .screenshot({
          path: testInfo.outputPath(`node-table-${colorScheme}-${width}.png`),
        })
    }
    const audit = await new AxeBuilder({ page })
      .withTags(["wcag2a", "wcag2aa", "wcag21aa"])
      .analyze()
    expect(audit.violations.map(({ id }) => id)).toEqual([])
    await page.getByText("Node list", { exact: true }).click()
    await expect(table).toHaveCount(0)
  })

  test(`${colorScheme} report dropdowns leave clear chevron padding at mobile and desktop widths`, async ({
    page,
  }, testInfo) => {
    await page.emulateMedia({ colorScheme })
    await openListedRun(page)
    await page.getByRole("tab", { name: "Reports", exact: true }).click()
    const reports = page.getByRole("region", {
      name: "Run reports",
      exact: true,
    })
    for (const width of [2000, 320]) {
      await page.setViewportSize({ width, height: 1000 })
      for (const name of ["Report kind", "Report tag"]) {
        const control = reports.getByRole("combobox", { name, exact: true })
        await expect(control).toHaveAttribute("data-slot", "native-select")
        const spacing = await control.evaluate((element) => {
          if (!(element instanceof HTMLSelectElement))
            throw new Error("Expected a native dropdown.")
          const style = getComputedStyle(element)
          const box = element.getBoundingClientRect()
          const icon = element
            .parentElement!.querySelector('[data-slot="native-select-icon"]')!
            .getBoundingClientRect()
          const context = document.createElement("canvas").getContext("2d")!
          context.font = style.font
          const textWidth = context.measureText(
            element.selectedOptions[0]!.text
          ).width
          return {
            padding: parseFloat(style.paddingRight),
            trailing: box.right - icon.right,
            clearance:
              icon.left -
              (box.left +
                parseFloat(style.borderLeftWidth) +
                parseFloat(style.paddingLeft) +
                textWidth),
            center: Math.abs(
              (icon.top + icon.bottom - box.top - box.bottom) / 2
            ),
            radius: style.borderTopRightRadius,
          }
        })
        expect(spacing.padding).toBeGreaterThanOrEqual(40)
        expect(spacing.trailing).toBeGreaterThanOrEqual(11)
        expect(spacing.clearance).toBeGreaterThanOrEqual(10)
        expect(spacing.center).toBeLessThanOrEqual(1)
        expect(spacing.radius).toBe("0px")
      }
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth)
      ).toBeLessThanOrEqual(width)
      await reports.screenshot({
        path: testInfo.outputPath(`dropdowns-${colorScheme}-${width}.png`),
      })
    }
    await openTeamRunList(page)
    const status = page.getByRole("combobox", {
      name: "Run status",
      exact: true,
    })
    await status.selectOption("Failed")
    await expect(status).toHaveValue("Failed")
    await expect(status).toHaveAttribute("data-slot", "native-select")
  })

  test(`${colorScheme} enabled actions use pointer cursors without making disabled controls or text fields clickable`, async ({
    page,
  }) => {
    await page.emulateMedia({ colorScheme })
    await openListedRun(page)
    const graphTab = page.getByRole("tab", { name: "Graph", exact: true })
    await graphTab.hover()
    await expect(graphTab).toHaveCSS("cursor", "pointer")
    await page.mouse.down()
    await expect(graphTab).toHaveCSS("cursor", "pointer")
    await page.mouse.up()
    await expect(page.locator("[data-graph-node]").first()).toHaveCSS(
      "cursor",
      "pointer"
    )
    const summary = page.getByText("Node list", { exact: true })
    await expect(summary).toHaveCSS("cursor", "pointer")
    await summary.click()
    const table = page.getByRole("table", { name: "Run nodes" })
    await expect(table.getByRole("button").first()).toHaveCSS(
      "cursor",
      "pointer"
    )
    await expect(table.getByRole("columnheader").first()).not.toHaveCSS(
      "cursor",
      "pointer"
    )
    await page.getByRole("tab", { name: "Metrics", exact: true }).click()
    const newer = page.getByRole("button", {
      name: "Newer samples",
      exact: true,
    })
    await expect(newer).toBeDisabled()
    await expect(newer).toHaveCSS("cursor", "not-allowed")
    await page.getByRole("tab", { name: "Reports", exact: true }).click()
    await expect(
      page.getByRole("combobox", { name: "Report kind", exact: true })
    ).toHaveCSS("cursor", "pointer")
    const account = page.getByRole("button", { name: /Account menu for / })
    await account.hover()
    await expect(account).toHaveCSS("cursor", "pointer")
    await expect(account.getByRole("img")).toHaveCSS("cursor", "pointer")
    await account.click()
    const menu = page.getByRole("menu")
    await expect(
      menu.getByRole("menuitem", { name: "Profile details", exact: true })
    ).toHaveCSS("cursor", "pointer")
    await expect(
      menu.getByRole("menuitem", { name: "Settings", exact: true })
    ).toHaveCSS("cursor", "pointer")
    await expect(
      menu.getByRole("menuitem", { name: "Sign out", exact: true })
    ).toHaveCSS("cursor", "not-allowed")
    await expect(
      menu.getByText("Development authentication", { exact: true })
    ).not.toHaveCSS("cursor", "pointer")
    await menu.getByRole("menuitem", { name: "Settings", exact: true }).click()
    const settings = page.getByRole("dialog", { name: "Settings", exact: true })
    await expect(
      settings.getByRole("radio", { name: "Dark", exact: true })
    ).toHaveCSS("cursor", "pointer")
    await settings.getByRole("button", { name: "Close account dialog" }).click()
    await page
      .getByRole("link", { name: "Task Plan for this run", exact: true })
      .click()
    await page.getByRole("button", { name: "Trigger run", exact: true }).click()
    const trigger = page.getByRole("dialog", {
      name: "Trigger workflow run",
      exact: true,
    })
    const checkbox = trigger.getByRole("checkbox", {
      name: "Run on a single machine",
    })
    await expect(checkbox).toHaveCSS("cursor", "pointer")
    await expect(checkbox.locator("..")).toHaveCSS("cursor", "pointer")
    await checkbox.click()
    await expect(checkbox).toBeChecked()
    await trigger.getByRole("button", { name: "Close", exact: true }).click()
    await openTeamRunList(page)
    await expect(page.getByRole("textbox").first()).not.toHaveCSS(
      "cursor",
      "pointer"
    )
    const missing = await page
      .locator(
        ':is(a[href],button,select,summary,[role="button"],[role="tab"],[role^="menuitem"]):not(:disabled):not([aria-disabled="true"]):not([data-disabled])'
      )
      .evaluateAll((elements) =>
        elements
          .filter(
            (element) =>
              element.getClientRects().length &&
              getComputedStyle(element).cursor !== "pointer"
          )
          .map(
            (element) => `${element.tagName}: ${element.textContent?.trim()}`
          )
      )
    expect(missing).toEqual([])
  })

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
          const shaft = element.getPointAtLength(length - 8)
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
            shaftLength: end.x - shaft.x,
            shaftRise: end.y - shaft.y,
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
      expect(edge.targetGap, edge.edge).toBeCloseTo(2)
      expect(edge.shaftLength, edge.edge).toBeCloseTo(8)
      expect(edge.shaftRise, edge.edge).toBeCloseTo(0)
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
