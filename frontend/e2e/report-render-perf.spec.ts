import { expect, test } from "@playwright/test"
import { execFileSync } from "node:child_process"
import { readFile, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { resolve } from "node:path"
import { z } from "zod"
import { seedRendererReport } from "../scripts/seed-renderer-reports"

function csvFixture(size: number) {
  const header = "name,value\n"
  let count = Math.floor((size - header.length) / 64)
  if (size - header.length - count * 64 < 6) count--
  const tail = size - header.length - count * 64
  const bytes = Buffer.from(
    header +
      Array.from(
        { length: count },
        (_, index) =>
          `row-${String(index).padStart(6, "0")},${"x".repeat(52)}\n`
      ).join("") +
      `tail,${"x".repeat(tail - 6)}\n`
  )
  expect(bytes.length).toBe(size)
  return { bytes, rows: count + 1 }
}

test("5/10 MB reports preserve bounded paging; Chromium meets one-second rendering and 60 fps scrolling", async ({
  page,
  request,
}) => {
  test.setTimeout(240_000)
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
  const created = await request.post("/api/v1/runs", {
    data: { workflowId: "report-tests/viewer/basic", variables: {} },
  })
  expect(created.status()).toBe(202)
  const { runId } = z.object({ runId: z.uuid() }).parse(await created.json())
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
    .poll(() => sql(`SELECT count(*) FROM runs WHERE id='${runId}'`))
    .toBe("1")
  const seed = (kind: string, title: string, bytes: Buffer) =>
    seedRendererReport(resolve(tmpdir(), fixture.directory), runId, sql, {
      kind,
      title,
      bytes,
    })
  const smallBytes = csvFixture(5_000_000)
  const small = await seed("csv", "Five MB table", smallBytes.bytes)
  const largeBytes = csvFixture(10_000_000)
  const large = await seed("csv", "Ten MB table", largeBytes.bytes)
  const text = await seed(
    "text",
    "Ten MB text",
    Buffer.from(
      "first text page\n" + "x".repeat(10_000_000 - "first text page\n".length)
    )
  )
  await page.addInitScript((reportId: string) => {
    const measurement = {
      start: 0,
      rendered: 0,
      workerCreated: 0,
      workerFinished: 0,
    }
    Reflect.set(window, "reportRenderMeasurement", measurement)
    const NativeWorker = window.Worker
    window.Worker = class extends NativeWorker {
      constructor(...args: ConstructorParameters<typeof Worker>) {
        super(...args)
        if (String(args[0]).includes("table.worker")) {
          measurement.workerCreated = performance.now()
          this.addEventListener("message", () => {
            measurement.workerFinished = performance.now()
          })
        }
      }
    }
    document.addEventListener(
      "click",
      (event) => {
        const target = event.target
        if (
          !(target instanceof Element) ||
          !target.closest(`a[href$="/reports/${reportId}"]`)
        )
          return
        measurement.start = performance.now()
        const observer = new MutationObserver(() => {
          if (!document.querySelector("table tbody tr[aria-rowindex]")) return
          measurement.rendered = performance.now()
          observer.disconnect()
        })
        observer.observe(document, { subtree: true, childList: true })
      },
      true
    )
  }, small.id)
  // Time a real SPA report click, including uncached HTTP bytes and parsing.
  await page.goto(`/runs/${runId}`)
  await page.getByRole("tab", { name: "Reports", exact: true }).click()
  await page.getByRole("link", { name: small.title, exact: true }).waitFor()
  const idleFrames = await page.evaluate(
    () =>
      new Promise<number[]>((resolve) => {
        const frames: number[] = []
        let previous = 0
        const frame = (now: number) => {
          if (previous) frames.push(now - previous)
          previous = now
          if (frames.length === 60) resolve(frames)
          else requestAnimationFrame(frame)
        }
        requestAnimationFrame(frame)
      })
  )
  await page.getByRole("link", { name: small.title, exact: true }).click()
  await expect(page.getByRole("table")).toHaveAttribute(
    "aria-rowcount",
    String(smallBytes.rows + 1)
  )
  const renderMs = await page.evaluate(() => {
    const value: { start: number; rendered: number } = Reflect.get(
      window,
      "reportRenderMeasurement"
    )
    return value.rendered - value.start
  })
  const breakdown = await page.evaluate(() => ({
    measurement: Reflect.get(window, "reportRenderMeasurement"),
    resources: performance
      .getEntriesByType("resource")
      .filter(
        (item) =>
          item.name.includes("/api/") || item.name.includes("table.worker")
      )
      .map((item) => item.toJSON()),
  }))
  const timingPath = test.info().outputPath("report-five-MB-timing.json")
  await writeFile(timingPath, JSON.stringify(breakdown, null, 2))
  await test.info().attach("report-five-MB-timing.json", {
    path: timingPath,
    contentType: "application/json",
  })
  expect(renderMs).toBeGreaterThan(0)
  if (test.info().project.name === "chromium")
    expect(renderMs).toBeLessThan(1000)
  await expect(
    page.getByRole("button", { name: "Next table page" })
  ).toHaveCount(0)
  await page.goto(`/runs/${runId}/reports/${large.id}`)
  const table = page.getByRole("table")
  const firstCell = table.locator("tbody tr[aria-rowindex] td").first()
  await expect(firstCell).toHaveText("row-000000")
  await expect(
    page.getByText(/More rows available; later pages are not yet validated/)
  ).toBeVisible()
  await expect(table.locator("tbody tr[aria-rowindex]")).toHaveCount(24)
  expect(Number(await table.getAttribute("aria-rowcount"))).toBeLessThan(
    largeBytes.rows
  )
  const frames = await page
    .getByRole("region", { name: "Ten MB table scrollable data" })
    .evaluate(
      (node) =>
        new Promise<number[]>((resolve) => {
          const times: number[] = []
          let previous = 0
          const frame = (now: number) => {
            if (previous) times.push(now - previous)
            previous = now
            node.scrollTop += 16
            if (times.length === 120) resolve(times)
            else requestAnimationFrame(frame)
          }
          requestAnimationFrame(frame)
        })
    )
  const sorted = frames.toSorted((left, right) => left - right)
  const medianFrameMs = sorted[Math.floor(sorted.length / 2)]!
  const p95FrameMs = sorted[Math.ceil(sorted.length * 0.95) - 1]!
  const metrics = {
    bytes: small.sizeBytes,
    renderMs,
    medianFrameMs,
    p95FrameMs,
    typicalFps: 1000 / medianFrameMs,
    averageFps:
      (1000 * frames.length) / frames.reduce((sum, time) => sum + time, 0),
    frames,
    idleFrames,
  }
  const metricsPath = test.info().outputPath("report-performance.json")
  await writeFile(metricsPath, JSON.stringify(metrics, null, 2))
  await test.info().attach("report-performance.json", {
    path: metricsPath,
    contentType: "application/json",
  })
  if (test.info().project.name === "chromium") {
    expect(Math.round(metrics.typicalFps)).toBeGreaterThanOrEqual(60)
    expect(Math.round(metrics.averageFps)).toBeGreaterThanOrEqual(60)
    expect(p95FrameMs).toBeLessThanOrEqual(20)
  }
  await page.getByRole("button", { name: "Next table page" }).click()
  await expect(firstCell).not.toHaveText("row-000000")
  const secondPageStart = await firstCell.textContent()
  await page.getByRole("button", { name: "Previous table page" }).click()
  await expect(firstCell).toHaveText("row-000000")
  await page.getByRole("button", { name: "Next table page" }).click()
  await expect(firstCell).toHaveText(secondPageStart!)
  for (;;) {
    const next = page.getByRole("button", { name: "Next table page" })
    if (await next.isDisabled()) break
    const current = await firstCell.textContent()
    await next.click()
    await expect(firstCell).not.toHaveText(current!)
    await expect(table.locator("tbody tr[aria-rowindex]")).toHaveCount(24)
  }
  await expect(page.getByText(/Report parsing complete/)).toHaveText(
    new RegExp(`${largeBytes.rows.toLocaleString("en-US")} rows parsed`)
  )
  await expect(
    page.getByRole("button", { name: "Next table page" })
  ).toBeDisabled()
  await page.getByRole("searchbox").fill("tail")
  await expect(
    page.getByRole("cell", { name: "tail", exact: true })
  ).toBeVisible()
  await page.goto(`/runs/${runId}/reports/${text.id}`)
  const textPage = page.getByRole("region", {
    name: "Ten MB text",
    exact: true,
  })
  await expect(textPage).toContainText("first text page")
  expect((await textPage.textContent())!.length).toBe(65_536)
  await expect(page.getByRole("status")).toHaveText(
    "Page 1; 65,536 of 10,000,000 bytes read."
  )
  await page.getByRole("button", { name: "Next text page" }).click()
  await expect(page.getByRole("status")).toHaveText(
    "Page 2; 131,072 of 10,000,000 bytes read."
  )
  await expect(textPage).not.toContainText("first text page")
  await page.getByRole("button", { name: "Previous text page" }).click()
  await expect(textPage).toContainText("first text page")
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth
    )
  ).toBe(true)
})
