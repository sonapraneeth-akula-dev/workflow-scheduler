import { writeFile } from "node:fs/promises"
import AxeBuilder from "@axe-core/playwright"
import { expect, test } from "@playwright/test"
import { z } from "zod"

const settings = z
  .object({
    runId: z.uuid(),
    token: z.string().min(1),
    marker: z.string().min(1),
  })
  .parse({
    runId: process.env.WF_SYSTEM_EVENTS_RUN,
    token: process.env.WF_SYSTEM_EVENTS_TOKEN,
    marker: process.env.WF_SYSTEM_EVENTS_READY,
  })
test("actual failover history keeps pinned vs selected agents, filters, pages and safe live remote transitions", async ({
  page,
}) => {
  test.setTimeout(120_000)
  let reads = 0
  const writes: string[] = []
  page.on("request", (request) => {
    const url = new URL(request.url())
    if (
      request.method() === "GET" &&
      url.pathname === `/api/v1/runs/${settings.runId}/system-events`
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
  await page.getByRole("tab", { name: "System Events", exact: true }).click()
  await expect(page.getByRole("table")).toBeVisible()
  await page.getByRole("link", { name: "Open System Events page" }).click()
  await expect(
    page.getByRole("heading", { level: 1, name: "Run System Events" })
  ).toBeVisible()
  const actual = z
    .object({
      runId: z.uuid(),
      versionId: z.uuid(),
      lastSeq: z.string(),
      events: z.array(
        z.object({
          seq: z.string(),
          entity: z.number(),
          state: z.number(),
          attempt: z.number().nullable(),
          agentId: z.string().nullable(),
          plannedAgentId: z.string().nullable(),
          selectionReason: z.string().nullable(),
          progress: z.object({ phase: z.number() }).nullable(),
        })
      ),
    })
    .parse(
      await page.evaluate(async (runId) => {
        const path = "/src/lib/api/client.ts"
        const { publicApi }: typeof import("../src/lib/api/client") =
          await import(path)
        const response = await publicApi.GET(
          "/api/v1/runs/{id}/system-events",
          {
            params: { path: { id: runId }, query: { limit: "200" } },
          }
        )
        if (!response.response.ok)
          throw new Error(`Actual events API: HTTP ${response.response.status}`)
        return response.data
      }, settings.runId)
    )
  expect(actual.runId).toBe(settings.runId)
  const dispatches = actual.events.filter(
    (event) => event.entity === 2 && event.state === 2 && !event.progress
  )
  expect(dispatches.map((event) => event.attempt)).toEqual([1, 2])
  expect(dispatches[0]?.agentId).not.toBe(dispatches[1]?.agentId)
  expect(dispatches[1]?.plannedAgentId).toBe(dispatches[0]?.agentId)
  expect(
    dispatches.every((event) => event.selectionReason === "LeastLoaded")
  ).toBe(true)
  async function loadHistory() {
    const more = page.getByRole("button", { name: "Load more system events" })
    while (await more.isVisible()) {
      await expect(more).toBeEnabled()
      const before = await page.getByRole("rowheader").count()
      await more.click()
      await expect
        .poll(() => page.getByRole("rowheader").count())
        .toBeGreaterThan(before)
    }
  }
  await loadHistory()
  const table = page.getByRole("table")
  await expect(table.getByRole("rowheader")).toHaveText(
    actual.events.map((event) => event.seq)
  )
  const replacement = table.locator(
    `tr[data-system-seq="${dispatches[1]!.seq}"]`
  )
  await expect(replacement.getByRole("cell").nth(4)).toHaveText(
    dispatches[0]!.agentId!
  )
  await expect(replacement.getByRole("cell").nth(5)).toHaveText(
    dispatches[1]!.agentId!
  )
  const pinned = page.getByRole("link", {
    name: "Pinned Task Plan",
    exact: true,
  })
  await expect(pinned).toHaveAttribute(
    "href",
    new RegExp(`/versions/${actual.versionId}$`)
  )
  await expect(
    page.getByRole("link", { name: "Declared task plan" }).first()
  ).toHaveAttribute("href", new RegExp(`/versions/${actual.versionId}/tasks/`))
  await page.getByLabel("Node key", { exact: true }).fill("node")
  await page.getByLabel("Full task key", { exact: true }).fill("node/task")
  await page.getByLabel("Attempt", { exact: true }).fill("2")
  await page.getByRole("button", { name: "Apply event filters" }).click()
  await expect(table.getByRole("rowheader")).toHaveText(
    actual.events
      .filter((event) => event.attempt === 2)
      .map((event) => event.seq)
  )
  await page.getByRole("button", { name: "Clear event filters" }).click()
  await expect(
    page.getByRole("button", { name: "Refresh system events" })
  ).toBeEnabled()
  await loadHistory()
  await expect(table.getByRole("rowheader")).toHaveText(
    actual.events.map((event) => event.seq)
  )
  await page.waitForLoadState("networkidle")
  const quiet = reads
  await page.waitForTimeout(600)
  expect(reads).toBe(quiet)
  await writeFile(settings.marker, settings.runId)
  await expect(
    table.getByRole("cell", { name: /^Remote: Succeeded Occurred / })
  ).toBeVisible()
  expect(reads).toBeGreaterThan(quiet)
  await expect(table.getByRole("rowheader")).toHaveCount(
    actual.events.length + 1
  )
  for (const secret of [
    "private-variable",
    "private-metadata",
    "private-error",
    "private-output",
    "packages.invalid",
  ])
    await expect(page.locator("body")).not.toContainText(secret)
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
  await page.getByLabel("Node key", { exact: true }).focus()
  await page.keyboard.press("Tab")
  await expect(page.getByLabel("Full task key", { exact: true })).toBeFocused()
  expect(writes).toEqual([])
})
