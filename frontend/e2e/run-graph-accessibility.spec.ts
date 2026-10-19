import AxeBuilder from "@axe-core/playwright"
import { expect, test } from "@playwright/test"
import { z } from "zod"

const settings = z
  .object({ run: z.uuid(), token: z.string().min(1) })
  .parse({ run: process.env.WF_GRAPH_RUN, token: process.env.WF_GRAPH_TOKEN })

test("keyboard graph navigation, icon/text statuses and list alternative are accessible", async ({
  page,
}) => {
  test.setTimeout(90_000)
  await page.goto("/")
  await page.evaluate(async (token) => {
    const path = "/browser-auth.js"
    const { hydrateBrowserSession }: typeof import("../scripts/browser-auth") =
      await import(path)
    await hydrateBrowserSession(token)
  }, settings.token)
  await page.goto(`/runs/${settings.run}`)
  const graph = page.getByRole("region", {
    name: "Run dependency graph",
    exact: true,
  })
  await expect(graph.locator("[data-graph-node]")).toHaveCount(200)
  const node = (key: string) => graph.locator(`[data-graph-node="${key}"]`)
  await expect(node("node-197")).toHaveAccessibleDescription(
    "1 task, sequential. Required node: hard-failure policy. Failure retries: 1/1 tasks; platform recovery: 0/1 tasks."
  )
  await expect(node("node-198")).toHaveAccessibleDescription(
    "1 task, sequential. Required node: hard-failure policy. No retries configured."
  )
  await expect(node("node-199")).toHaveAccessibleDescription(
    "2 tasks, sequential. Optional node: soft-failure policy; still runs. Failure retries: 1/2 tasks; platform recovery: 1/2 tasks."
  )
  await expect(
    node("node-199").getByTitle(
      "Optional node: soft-failure policy; still runs."
    )
  ).toBeVisible()
  await expect(
    node("node-199").getByTitle(
      "Failure retries: 1/2 tasks; platform recovery: 1/2 tasks."
    )
  ).toBeVisible()
  await expect(graph.locator('[data-graph-node][tabindex="0"]')).toHaveCount(1)
  await node("node-000").focus()
  await page.keyboard.press("ArrowDown")
  await expect(node("node-001")).toBeFocused()
  await page.keyboard.press("ArrowRight")
  await expect(node("node-011")).toBeFocused()
  await page.keyboard.press("ArrowUp")
  await expect(node("node-010")).toBeFocused()
  await page.keyboard.press("ArrowLeft")
  await expect(node("node-000")).toBeFocused()
  await page.keyboard.press("End")
  await expect(node("node-199")).toBeFocused()
  const viewport = await graph.evaluate((element) => ({
    x: element.scrollLeft,
    y: element.scrollTop,
  }))
  expect(viewport.x).toBeGreaterThan(0)
  expect(viewport.y).toBeGreaterThan(0)
  await page.keyboard.press("Enter")
  await expect(node("node-199")).toHaveAttribute("aria-pressed", "true")
  let details = page.getByRole("region", { name: "Selected node node-199" })
  await expect(details).toContainText("node-189 (OnSuccess)")
  await expect(details).toContainText(
    "2 tasks run sequentially, in the order below."
  )
  const tasks = details.getByRole("list", { name: "Tasks in node node-199" })
  await expect(tasks.getByRole("listitem")).toHaveCount(2)
  await expect(tasks.getByRole("listitem").first()).toContainText("work")
  await expect(tasks.getByRole("listitem").first()).toContainText(
    "Failure retries: disabled. Platform recovery: enabled for agent/platform failures."
  )
  await expect(tasks.getByRole("listitem").last()).toContainText("notify")
  await expect(tasks.getByRole("listitem").last()).toContainText(
    "Soft-failure task."
  )
  await expect(tasks.getByRole("listitem").last()).toContainText(
    "Failure retries: up to 2 (TaskError, Timeout). Platform recovery: disabled."
  )
  await expect(
    details.getByRole("link", { name: "View task plan for work" })
  ).toHaveAttribute("href", /\/tasks\/node-199%2Fwork$/)
  await page.keyboard.press("Home")
  await expect(node("node-000")).toBeFocused()
  await page.keyboard.press("Space")
  await expect(node("node-000")).toHaveAttribute("aria-pressed", "true")
  await page.keyboard.press("Tab")
  const summary = page.getByText("Node list", { exact: true })
  await expect(summary).toBeFocused()
  await page.keyboard.press("Enter")
  const table = page.getByRole("table", { name: "Run nodes" })
  await expect(table.getByRole("button")).toHaveCount(200)
  await table
    .getByRole("button", { name: "Inspect node node-199", exact: true })
    .focus()
  await page.keyboard.press("Enter")
  details = page.getByRole("region", { name: "Selected node node-199" })
  await expect(details).toContainText("node-188 (OnSuccess)")
  const statuses = await graph
    .locator("[data-graph-node]")
    .evaluateAll((nodes) =>
      nodes.every((node) => {
        const badge = node.querySelector('[data-slot="badge"]')
        return (
          !!badge?.textContent?.trim() &&
          badge.querySelector("svg")?.getAttribute("aria-hidden") === "true"
        )
      })
    )
  expect(statuses).toBe(true)
  for (const dark of [false, true]) {
    await page.setViewportSize(
      dark ? { width: 375, height: 812 } : { width: 1440, height: 1080 }
    )
    await page.evaluate(
      (dark) => document.documentElement.classList.toggle("dark", dark),
      dark
    )
    const result = await new AxeBuilder({ page })
      .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
      .analyze()
    expect(
      result.violations.filter(
        (issue) => issue.impact === "critical" || issue.impact === "serious"
      )
    ).toEqual([])
    const widths = await page.evaluate(() => ({
      viewport: innerWidth,
      page: document.documentElement.scrollWidth,
    }))
    expect(widths.page).toBeLessThanOrEqual(widths.viewport)
    await page.screenshot({
      path: test
        .info()
        .outputPath(
          `graph-policies-${dark ? "mobile-dark" : "desktop-light"}.png`
        ),
      fullPage: true,
    })
  }
})
