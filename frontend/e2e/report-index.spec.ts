import AxeBuilder from "@axe-core/playwright"
import { expect, test } from "@playwright/test"
import { execFileSync } from "node:child_process"
import { readFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { resolve } from "node:path"
import { z } from "zod"
import {
  seedRendererReport,
  seedRendererReports,
} from "../scripts/seed-renderer-reports"

test("Report subtabs retain sources, filter exact kind/tag and receive a committed addition within one second without navigation", async ({
  page,
  request,
}, testInfo) => {
  test.setTimeout(90_000)
  const fixture = z
    .object({
      directory: z.string().regex(/^wf-ui-[a-zA-Z0-9_-]+$/),
      container: z.string().regex(/^wf-ui-[a-f0-9-]+$/),
    })
    .parse(
      JSON.parse(
        await readFile(new URL("../.test-stack.json", import.meta.url), "utf8")
      )
    )
  const triggered = await request.post("/api/v1/runs", {
    data: {
      workflowId: "report-tests/viewer/basic",
      variables: {},
    },
  })
  expect(triggered.status()).toBe(202)
  const { runId } = z.object({ runId: z.uuid() }).parse(await triggered.json())
  const sql = (statement: string) =>
    execFileSync(
      "docker",
      [
        "exec",
        fixture.container,
        "psql",
        "-U",
        "workflow",
        "-d",
        "workflow",
        "-At",
        "-v",
        "ON_ERROR_STOP=1",
        "-c",
        statement,
      ],
      { encoding: "utf8", timeout: 20_000 }
    ).trim()
  await expect
    .poll(() => sql(`SELECT count(*) FROM runs WHERE id='${runId}'`), {
      timeout: 20_000,
    })
    .toBe("1")
  const storage = resolve(tmpdir(), fixture.directory)
  const initial = await seedRendererReports(storage, runId, sql)
  let reads = 0
  const sockets: string[] = []
  page.on("websocket", (socket) => sockets.push(socket.url()))
  page.on("request", (item) => {
    if (new URL(item.url()).pathname === `/api/v1/runs/${runId}/reports`)
      reads++
  })
  await page.goto(`/runs/${runId}`)
  const graph = page.getByRole("tab", { name: "Graph", exact: true })
  await expect(graph).toHaveAttribute("aria-selected", "true")
  expect(reads).toBe(0)
  const reports = page.getByRole("tab", { name: "Reports", exact: true })
  await reports.click()
  await expect(
    page.getByText("Report updates live.", { exact: true })
  ).toBeVisible()
  await expect(
    page.getByText(`${initial.length} of ${initial.length} reports`)
  ).toBeVisible()
  const reportTabs = page.getByRole("tablist", { name: "Run report tabs" })
  await expect(reportTabs.getByRole("tab")).toHaveCount(initial.length)
  await expect(reportTabs).toContainText("$post/report-renderer")
  await page.getByRole("combobox", { name: "Report kind" }).selectOption("json")
  await expect(page.getByText(`2 of ${initial.length} reports`)).toBeVisible()
  await expect(
    reportTabs.getByRole("tab", { name: /^Metrics tree / })
  ).toBeVisible()
  await expect(
    reportTabs.getByRole("tab", { name: /^Release summary / })
  ).toHaveCount(0)
  await page.getByRole("button", { name: "Clear report filters" }).click()
  await expect(
    page.getByText(`${initial.length} of ${initial.length} reports`)
  ).toBeVisible()
  const before = reads
  await page.waitForTimeout(600)
  expect(reads).toBe(before)
  expect(
    sockets.filter((url) => new URL(url).pathname === "/hubs/live/v1")
  ).toHaveLength(1)
  const started = performance.now()
  const added = await seedRendererReport(storage, runId, sql, {
    kind: "text",
    title: `Live ${testInfo.project.name} result`,
    bytes: Buffer.from("actual committed report index content"),
    sourceKey: "build/live",
    tags: ["live", "review"],
  })
  const tab = reportTabs.getByRole("tab", {
    name: `${added.title} build/live | Attempt 1`,
    exact: true,
  })
  await expect(tab).toBeVisible()
  const elapsedMs = performance.now() - started
  expect(elapsedMs).toBeLessThanOrEqual(1000)
  await testInfo.attach("report-index-commit-to-visible.json", {
    body: JSON.stringify({
      elapsedMs,
      includesFixtureBlobWriteAndCommit: true,
    }),
    contentType: "application/json",
  })
  await expect(
    page.getByText(`${initial.length + 1} of ${initial.length + 1} reports`)
  ).toBeVisible()
  await expect(tab).toContainText("build/live")
  await page.getByRole("combobox", { name: "Report tag" }).selectOption("live")
  await expect(
    page.getByText(`1 of ${initial.length + 1} reports`)
  ).toBeVisible()
  await page.getByRole("combobox", { name: "Report kind" }).selectOption("json")
  await expect(page.getByText("No reports match these filters.")).toBeVisible()
  await page.getByRole("button", { name: "Clear report filters" }).click()
  for (const dark of [false, true]) {
    await page.setViewportSize(
      dark ? { width: 375, height: 812 } : { width: 1440, height: 1080 }
    )
    await page.evaluate(async (dark) => {
      document.documentElement.classList.toggle("dark", dark)
      await new Promise<void>((done) => requestAnimationFrame(() => done()))
      await Promise.all(
        document.getAnimations().map((animation) => animation.finished)
      )
    }, dark)
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
  await tab.click()
  await expect(page.getByRole("region", { name: added.title })).toHaveText(
    "actual committed report index content"
  )
  await expect(page).toHaveURL(new RegExp(`/runs/${runId}$`))
})
