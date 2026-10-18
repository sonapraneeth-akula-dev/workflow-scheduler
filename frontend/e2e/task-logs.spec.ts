import { mkdir, readFile, writeFile } from "node:fs/promises"
import { resolve } from "node:path"
import AxeBuilder from "@axe-core/playwright"
import { expect, test } from "@playwright/test"
import { z } from "zod"
import { analyzeGraphTrace } from "../scripts/graph-trace"

test.use({ launchOptions: { args: ["--disable-frame-rate-limit"] } })
const settings = z
  .object({
    run: z.uuid(),
    token: z.string().min(1),
    artifacts: z.string().min(1),
  })
  .parse({
    run: process.env.WF_LOGS_RUN,
    token: process.env.WF_LOGS_TOKEN,
    artifacts: process.env.WF_LOGS_ARTIFACTS,
  })

test("million-line archived retry opens below one second, paints 60fps and keeps live attempts isolated", async ({
  page,
  browser,
}) => {
  test.setTimeout(120_000)
  await page.setViewportSize({ width: 1440, height: 1080 })
  await page.goto("/")
  await page.evaluate(async (token) => {
    const path = "/browser-auth.js"
    const { hydrateBrowserSession }: typeof import("../scripts/browser-auth") =
      await import(path)
    await hydrateBrowserSession(token)
  }, settings.token)
  await page.goto(`/runs/${settings.run}`)
  await page.getByRole("tab", { name: "Logs", exact: true }).click()
  const section = page.getByRole("region", { name: "Task logs", exact: true })
  const selector = section.getByLabel("Log attempt")
  await expect(selector).toHaveValue('["work/task",2]')
  await expect(
    section.getByRole("status").filter({ hasText: "Live logs: live" })
  ).toBeVisible()
  const ranges: { offset: number; length: number }[] = []
  page.on("request", (request) => {
    const url = new URL(request.url())
    if (request.method() === "GET" && url.pathname.endsWith("/1/logs"))
      ranges.push({
        offset: Number(url.searchParams.get("offset")),
        length: Number(url.searchParams.get("length")),
      })
  })
  const openMs = await selector.evaluate(async (element) => {
    if (!(element instanceof HTMLSelectElement))
      throw new Error("Log attempt selector is not native.")
    const start = performance.now()
    element.value = '["work/task",1]'
    element.dispatchEvent(new Event("change", { bubbles: true }))
    await new Promise<void>((resolve, reject) => {
      function check() {
        if (
          document
            .querySelector('[data-log-line="1"]')
            ?.textContent?.includes("record 0000001")
        ) {
          requestAnimationFrame(() => resolve())
        } else if (performance.now() - start > 5000) {
          reject(new Error("Actual archived log did not render."))
        } else requestAnimationFrame(check)
      }
      requestAnimationFrame(check)
    })
    return performance.now() - start
  })
  const viewport = section.getByRole("log", { name: "Task log lines" })
  await expect(
    section.getByRole("status").filter({ hasText: "1,000,000 log records" })
  ).toBeVisible()
  expect(openMs).toBeLessThan(1000)
  await expect(viewport.locator("[data-log-line]")).toHaveCount(36)
  expect(await viewport.locator("script").count()).toBe(0)
  await mkdir(settings.artifacts, { recursive: true })
  const session = await page.context().newCDPSession(page)
  const { frameTree } = z
    .object({
      frameTree: z.object({ frame: z.object({ id: z.string() }) }),
    })
    .parse(await session.send("Page.getFrameTree"))
  const tracePath = resolve(settings.artifacts, "chrome-trace.json")
  await session.send("Tracing.start", {
    categories:
      "devtools.timeline,disabled-by-default-devtools.timeline,disabled-by-default-devtools.timeline.frame,blink.user_timing",
    transferMode: "ReturnAsStream",
  })
  let scrolled: {
    frames: number
    missingFrames: number
    maximumRows: number
    lastScrollTop: number
  }
  try {
    scrolled = await viewport.evaluate(async (element) => {
      if (!(element instanceof HTMLElement))
        throw new Error("Log viewport is absent.")
      const start = performance.now()
      let frames = 0,
        missingFrames = 0,
        maximumRows = 0
      performance.mark("logs-load-start")
      await new Promise<void>((resolve) => {
        function scroll(now: number) {
          const box = element.getBoundingClientRect()
          const rows = Array.from(
            element.querySelectorAll<HTMLElement>("[data-log-line]")
          )
          maximumRows = Math.max(maximumRows, rows.length)
          const visible = rows.filter((row) => {
            const rect = row.getBoundingClientRect()
            return rect.bottom > box.top + 1 && rect.top < box.bottom - 1
          })
          if (
            visible.length < 19 ||
            visible.some((row) => !row.textContent?.includes("masked ***"))
          )
            missingFrames++
          frames++
          element.scrollTop += 24
          if (now - start < 6000) requestAnimationFrame(scroll)
          else resolve()
        }
        requestAnimationFrame(scroll)
      })
      performance.mark("logs-load-end")
      return {
        frames,
        missingFrames,
        maximumRows,
        lastScrollTop: element.scrollTop,
      }
    })
  } finally {
    const completed = new Promise<unknown>((resolve) =>
      session.once("Tracing.tracingComplete", resolve)
    )
    await session.send("Tracing.end")
    const result = z
      .object({ stream: z.string(), dataLossOccurred: z.boolean().optional() })
      .parse(await completed)
    const parts: Buffer[] = []
    try {
      while (true) {
        const part = z
          .object({
            data: z.string(),
            base64Encoded: z.boolean().optional(),
            eof: z.boolean(),
          })
          .parse(await session.send("IO.read", { handle: result.stream }))
        parts.push(
          Buffer.from(part.data, part.base64Encoded ? "base64" : "utf8")
        )
        if (part.eof) break
      }
    } finally {
      await session.send("IO.close", { handle: result.stream })
    }
    await writeFile(tracePath, Buffer.concat(parts))
    expect(result.dataLossOccurred ?? false).toBe(false)
  }
  const frames = analyzeGraphTrace(
    JSON.parse(await readFile(tracePath, "utf8")),
    frameTree.frame.id,
    "logs"
  )
  const report = {
    browserVersion: browser.version(),
    lines: 1000000,
    openMs,
    renderingMode:
      "Uncapped Chrome rendering capacity, not display refresh rate",
    transport:
      "Production signed-JWT HTTP ranges / actual immutable gzip blobs / PostgreSQL authorization",
    ...scrolled,
    ...frames,
    animationFramesPerSecond: scrolled.frames / frames.loadWindowSeconds,
  }
  await writeFile(
    resolve(settings.artifacts, "report.json"),
    JSON.stringify(report, null, 2)
  )
  await test.info().attach("Chrome log performance", {
    path: tracePath,
    contentType: "application/json",
  })
  console.log(JSON.stringify(report))
  expect(frames.fps).toBeGreaterThanOrEqual(60)
  expect(report.animationFramesPerSecond).toBeGreaterThanOrEqual(60)
  expect(scrolled.missingFrames).toBe(0)
  expect(scrolled.maximumRows).toBeLessThanOrEqual(36)
  expect(scrolled.lastScrollTop).toBeGreaterThan(24 * 100)
  expect(ranges.length).toBeGreaterThan(1)
  expect(ranges.every((range) => range.length === 400)).toBe(true)
  await viewport.focus()
  await page.keyboard.press("End")
  await expect(viewport.locator('[data-log-line="1000000"]')).toContainText(
    "record 1000000"
  )
  await page.keyboard.press("Home")
  await expect(viewport.locator('[data-log-line="1"]')).toContainText(
    "record 0000001"
  )
  await selector.selectOption('["work/task",2]')
  await expect(viewport).not.toContainText("record 0000001")
  const produced = await page.request.post("/api/test/logs/emit", {
    headers: { Authorization: `Bearer ${settings.token}` },
  })
  expect(produced.ok()).toBe(true)
  const emitted = z
    .object({ at: z.iso.datetime({ offset: true }), line: z.number().int() })
    .parse(await produced.json())
  await expect(viewport.locator('[data-log-line="1"]')).toContainText(
    "live-browser-1 *** <script>never execute</script>"
  )
  const tailLagMs = Date.now() - Date.parse(emitted.at)
  expect(tailLagMs).toBeLessThanOrEqual(1000)
  expect(await viewport.locator("script").count()).toBe(0)
  await page.getByRole("button", { name: "Pause live logs" }).click()
  await expect(
    section.getByRole("status").filter({ hasText: "Live logs: paused" })
  ).toBeVisible()
  const axe = await new AxeBuilder({ page })
    .include('section[aria-label="Task logs"]')
    .analyze()
  expect(axe.violations).toEqual([])
  await writeFile(
    resolve(settings.artifacts, "tail.json"),
    JSON.stringify(
      { tailLagMs, emittedLine: emitted.line, axeViolations: 0 },
      null,
      2
    )
  )
})
