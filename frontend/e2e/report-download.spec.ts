import AxeBuilder from "@axe-core/playwright"
import { expect, test } from "@playwright/test"
import { readFile } from "node:fs/promises"
import { z } from "zod"

test("unknown report kind downloads original binary bytes without executing active content", async ({
  page,
}) => {
  const fixture = z
    .object({
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
    .parse(
      JSON.parse(
        await readFile(new URL("../.test-stack.json", import.meta.url), "utf8")
      )
    )
  const report = fixture.rendererReports.find(
    (item) => item.kind === "unregistered"
  )
  if (!report) throw new Error("Unknown-kind report fixture is missing.")
  await page.goto(`/runs/${fixture.rendererRunId}/reports/${report.id}`)
  const link = page.getByRole("link", { name: `Download ${report.title}` })
  await expect(link).toBeVisible()
  await expect(link).toHaveAttribute("href", /^blob:/)
  await expect(page.getByRole("heading", { name: report.title })).toBeVisible()
  await expect(
    page.getByText(`${report.sizeBytes} bytes`, { exact: true })
  ).toBeVisible()
  const received = page.waitForEvent("download")
  await link.click()
  const download = await received
  expect(download.suggestedFilename()).toBe(report.title)
  expect(await download.failure()).toBeNull()
  const path = await download.path()
  if (!path) throw new Error("Browser did not persist the report download.")
  const bytes = await readFile(path)
  expect(bytes.length).toBe(report.sizeBytes)
  expect(bytes.toString("utf8")).toBe(
    "<script>window.reportScriptRan=true</script>"
  )
  expect(
    await page.evaluate(() => Reflect.get(window, "reportScriptRan"))
  ).toBeUndefined()
  for (const dark of [false, true]) {
    await page.setViewportSize(
      dark ? { width: 375, height: 812 } : { width: 1440, height: 1080 }
    )
    await page.evaluate(async (dark) => {
      document.documentElement.classList.toggle("dark", dark)
      await new Promise<void>((resolve) =>
        requestAnimationFrame(() => resolve())
      )
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
})
