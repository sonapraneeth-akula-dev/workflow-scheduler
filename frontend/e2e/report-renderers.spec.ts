import AxeBuilder from "@axe-core/playwright"
import { expect, test } from "@playwright/test"
import { readFile } from "node:fs/promises"
import { z } from "zod"

const state = z.object({
  rendererRunId: z.uuid(),
  rendererReports: z.array(
    z.object({
      id: z.uuid(),
      kind: z.string(),
      title: z.string(),
      sizeBytes: z.number(),
    })
  ),
})

test("committed reports render from real authenticated bytes with safe Markdown, JSON expansion, raster decoding and error isolation", async ({
  page,
}) => {
  test.setTimeout(90_000)
  const fixture = state.parse(
    JSON.parse(
      await readFile(new URL("../.test-stack.json", import.meta.url), "utf8")
    )
  )
  const requests: string[] = []
  page.on("request", (request) => requests.push(request.url()))
  for (const report of fixture.rendererReports.filter(
    (item) => item.kind !== "unregistered"
  )) {
    const contentRead = page.waitForResponse((response) =>
      response.url().endsWith(`/api/v1/reports/${report.id}/content`)
    )
    await page.goto(`/runs/${fixture.rendererRunId}/reports/${report.id}`)
    const content = await contentRead
    expect(content.status()).toBe(200)
    expect(content.headers()["cache-control"]).toContain("no-store")
    expect(content.headers()["x-content-type-options"]).toBe("nosniff")
    const byteSize = await page.evaluate(
      async ({ runId, reportId }) => {
        const path = "/src/features/reports/api.ts"
        const { getReportView }: typeof import("../src/features/reports/api") =
          await import(path)
        return (await getReportView(runId, reportId)).content.size
      },
      { runId: fixture.rendererRunId, reportId: report.id }
    )
    expect(byteSize).toBe(report.sizeBytes)
    await expect(
      page.getByRole("heading", { name: report.title, exact: true })
    ).toBeVisible()
    if (report.title === "Release summary") {
      await expect(
        page.getByRole("heading", { name: "Report result" })
      ).toBeVisible()
      await expect(page.getByText("Passed", { exact: true })).toBeVisible()
      await expect(
        page.getByText("Tracking (inline image blocked)")
      ).toBeVisible()
      await expect(page.getByText("Unsafe (link blocked)")).toBeVisible()
      await expect(
        page.getByRole("link", { name: "Documentation" })
      ).toHaveAttribute("rel", "noopener noreferrer")
      await expect(
        page.locator("article script, article iframe, article img")
      ).toHaveCount(0)
    } else if (report.title === "Build output") {
      const text = page.getByRole("region", { name: report.title })
      await expect(text).toHaveText(
        'first\n  second <script>alert("x")</script> \u03bb'
      )
      await text.focus()
      await expect(text).toBeFocused()
    } else if (report.title === "Metrics tree") {
      const special = page.getByText('"__proto__": Object (1)', { exact: true })
      await expect(special).toBeVisible()
      await expect(page.getByText('"nested":', { exact: true })).toHaveCount(0)
      await special.focus()
      await page.keyboard.press("Enter")
      await expect(page.getByText('"nested":', { exact: true })).toBeVisible()
      await page.getByText('"values": Array (4)', { exact: true }).click()
      await expect(
        page.getByText('"<script>\\n"', { exact: true })
      ).toBeVisible()
    } else if (report.title === "Raster screenshot") {
      const image = page.getByRole("img", { name: report.title })
      await expect(image).toBeVisible()
      await expect
        .poll(() =>
          image.evaluate(
            (node) =>
              node instanceof HTMLImageElement &&
              node.complete &&
              node.naturalWidth === 1
          )
        )
        .toBe(true)
      await expect(image).toHaveAttribute("src", /^blob:/)
    } else {
      await expect(page.getByRole("alert")).toContainText(
        report.title === "Truncated PNG"
          ? "Report image could not be decoded."
          : report.kind === "json"
            ? "Report content is not valid JSON."
            : "Report content is not a supported raster image."
      )
    }
    expect(
      await page.evaluate(() => Reflect.get(window, "reportScriptRan"))
    ).toBeUndefined()
    for (const dark of [false, true]) {
      await page.setViewportSize(
        dark ? { width: 375, height: 812 } : { width: 1440, height: 1080 }
      )
      await page.evaluate(
        (dark) => document.documentElement.classList.toggle("dark", dark),
        dark
      )
      await page.evaluate(async () => {
        await new Promise<void>((resolve) =>
          requestAnimationFrame(() => resolve())
        )
        await Promise.all(
          document.getAnimations().map((animation) => animation.finished)
        )
      })
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
  }
  expect(
    requests.some(
      (url) =>
        url.includes("example.invalid") || url.endsWith("/api/v1/private")
    )
  ).toBe(false)
})
