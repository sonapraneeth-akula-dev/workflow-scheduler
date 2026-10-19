import AxeBuilder from "@axe-core/playwright"
import { expect, test } from "@playwright/test"
import { z } from "zod"
import { formatTimestamp } from "../src/lib/timestamps"

const settings = z.object({ runId: z.uuid(), token: z.string().min(1) }).parse({
  runId: process.env.WF_TIMELINE_RUN,
  token: process.env.WF_TIMELINE_TOKEN,
})
test("actual retried run timeline separates queue/execution and retains accessible exact attempt/agent history", async ({
  page,
}) => {
  test.setTimeout(60_000)
  let reads = 0
  const writes: string[] = []
  page.on("request", (request) => {
    const url = new URL(request.url())
    if (
      request.method() === "GET" &&
      url.pathname === `/api/v1/runs/${settings.runId}/timeline`
    )
      reads++
    if (request.method() !== "GET" && url.pathname.startsWith("/api/"))
      writes.push(request.method() + " " + url.pathname)
  })
  await page.goto("/")
  await page.evaluate(async (jwt) => {
    const path = "/scripts/browser-auth.ts"
    const { hydrateBrowserSession }: typeof import("../scripts/browser-auth") =
      await import(path)
    await hydrateBrowserSession(jwt)
  }, settings.token)
  await page.goto(`/runs/${settings.runId}`)
  await expect(
    page.getByRole("button", { name: /^Account menu for/ })
  ).toBeVisible()
  const graphTab = page.getByRole("tab", { name: "Graph", exact: true })
  const timelineTab = page.getByRole("tab", { name: "Timeline", exact: true })
  await expect(graphTab).toHaveAttribute("aria-selected", "true")
  await expect.poll(() => reads).toBe(1)
  await graphTab.focus()
  await page.keyboard.press("ArrowRight")
  await expect(timelineTab).toBeFocused()
  await expect(timelineTab).toHaveAttribute("aria-selected", "true")
  const table = page.getByRole("table")
  await expect(table).toBeVisible()
  await expect.poll(() => reads).toBe(2)
  const actual = z
    .object({
      runId: z.uuid(),
      lastSeq: z.number(),
      attempts: z.array(
        z.object({
          taskKey: z.string(),
          attempt: z.number(),
          agentId: z.string().nullable(),
          resumedFrom: z.string().nullable(),
          queuedAt: z.string(),
          dispatchedAt: z.string(),
          startedAt: z.string().nullable(),
          endedAt: z.string().nullable(),
          queueWaitMs: z.number(),
          durationMs: z.number(),
          terminalStatus: z.string().nullable(),
        })
      ),
    })
    .parse(
      await page.evaluate(async (runId) => {
        const path = "/src/lib/api/client.ts"
        const { publicApi }: typeof import("../src/lib/api/client") =
          await import(path)
        const response = await publicApi.GET("/api/v1/runs/{id}/timeline", {
          params: { path: { id: runId } },
        })
        if (!response.response.ok)
          throw new Error(
            `Actual timeline read failed: HTTP ${response.response.status}`
          )
        return response.data
      }, settings.runId)
    )
  expect(actual.runId).toBe(settings.runId)
  const retries = new Map<string, number>()
  for (const row of actual.attempts)
    retries.set(row.taskKey, (retries.get(row.taskKey) ?? 0) + 1)
  expect([...retries.values()].some((count) => count >= 2)).toBe(true)
  expect(actual.attempts.every((row) => row.agentId !== null)).toBe(true)
  expect(actual.attempts.some((row) => row.resumedFrom !== null)).toBe(true)
  expect(actual.attempts[0]?.resumedFrom).toBeNull()
  const timezone = await page.evaluate(
    () => Intl.DateTimeFormat().resolvedOptions().timeZone
  )
  const localTime = (at: string) => formatTimestamp(at, timezone)
  await expect(
    table.getByRole("columnheader", { name: "Resumed from checkpoint (local)" })
  ).toBeVisible()
  await expect(table.getByRole("row")).toHaveCount(actual.attempts.length + 1)
  for (const [index, attempt] of actual.attempts.entries()) {
    const row = table.getByRole("row").nth(index + 1)
    await expect(row.getByRole("rowheader")).toHaveText(attempt.taskKey)
    await expect(row.getByRole("cell").nth(0)).toHaveText(
      String(attempt.attempt)
    )
    await expect(row.getByRole("cell").nth(1)).toHaveText(attempt.agentId!)
    await expect(row.getByRole("cell").nth(2)).toHaveText(
      attempt.resumedFrom ? localTime(attempt.resumedFrom) : "Not recorded"
    )
    if (attempt.resumedFrom)
      await expect(
        row.locator(`time[datetime="${attempt.resumedFrom}"]`)
      ).toBeVisible()
    await expect(row).toContainText(localTime(attempt.queuedAt))
    await expect(row).toContainText(localTime(attempt.dispatchedAt))
    if (attempt.startedAt)
      await expect(row).toContainText(localTime(attempt.startedAt))
    if (attempt.endedAt)
      await expect(row).toContainText(localTime(attempt.endedAt))
    await expect(row).toContainText(`${attempt.queueWaitMs} ms`)
    await expect(row).toContainText(`${attempt.durationMs} ms`)
    if (attempt.terminalStatus)
      await expect(row).toContainText(attempt.terminalStatus)
  }
  expect(await page.locator("[data-timeline-queue]").count()).toBe(
    actual.attempts.length
  )
  expect(
    await page.locator("[data-timeline-execution]").count()
  ).toBeGreaterThan(0)
  await page.waitForLoadState("networkidle")
  const before = reads
  await page.waitForTimeout(600)
  expect(reads).toBe(before)
  await page
    .getByRole("button", { name: "Refresh timeline", exact: true })
    .click()
  await expect.poll(() => reads).toBe(before + 1)
  await expect(table).toBeVisible()
  for (const dark of [false, true]) {
    await page.setViewportSize(
      dark ? { width: 375, height: 812 } : { width: 1440, height: 1080 }
    )
    await page.evaluate(
      (dark) => document.documentElement.classList.toggle("dark", dark),
      dark
    )
    const axe = await new AxeBuilder({ page })
      .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
      .analyze()
    expect(
      axe.violations.filter(
        (issue) => issue.impact === "critical" || issue.impact === "serious"
      )
    ).toEqual([])
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth
      )
    ).toBe(true)
  }
  const summary = page.getByText("Exact attempt table", { exact: true })
  await summary.focus()
  await page.keyboard.press("Enter")
  await expect(table).toBeHidden()
  await page.keyboard.press("Enter")
  await expect(table).toBeVisible()
  await timelineTab.focus()
  await page.keyboard.press("ArrowLeft")
  await expect(graphTab).toBeFocused()
  await expect(graphTab).toHaveAttribute("aria-selected", "true")
  await expect(
    page.getByRole("tabpanel", { name: "Graph", exact: true })
  ).toBeVisible()
  expect(writes).toEqual([])
})
