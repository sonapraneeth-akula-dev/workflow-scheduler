import { readFile } from "node:fs/promises"
import { randomUUID } from "node:crypto"
import AxeBuilder from "@axe-core/playwright"
import { expect, test, type Page } from "@playwright/test"
import { z } from "zod"
import { scheduleSchema, seriesFiresSchema } from "../src/lib/api/client"
import { parseJson } from "../src/lib/json"

async function accessible(page: Page) {
  const results = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
    .analyze()
  expect(
    results.violations.filter(
      (issue) => issue.impact === "critical" || issue.impact === "serious"
    )
  ).toEqual([])
}

test("retained skipped fires page through the real API and remain accessible on mobile", async ({
  page,
  request,
}) => {
  const fixture = z
    .object({ historyScheduleId: z.uuid(), historySeriesId: z.uuid() })
    .parse(
      JSON.parse(
        await readFile(new URL("../.test-stack.json", import.meta.url), "utf8")
      )
    )
  await page.goto(`/schedules/${fixture.historyScheduleId}`)
  await page.getByRole("link", { name: "View series history" }).click()
  await expect(page).toHaveURL(`/series/${fixture.historySeriesId}`)
  const table = page.getByRole("table")
  await expect(table.getByRole("row")).toHaveCount(51)
  await expect(table.getByRole("row").nth(1)).toContainText("Skipped")
  await expect(table.getByRole("row").nth(1)).toContainText("overlap")
  await expect(table.getByRole("row").nth(1)).toContainText("No run")
  await accessible(page)
  await page.getByRole("button", { name: "Load older fires" }).click()
  await expect(table.getByRole("row")).toHaveCount(61)
  await expect(
    page.getByRole("button", { name: "Load older fires" })
  ).toBeHidden()
  const response = await request.get(
    `/api/v1/series/${fixture.historySeriesId}/fires?limit=100`
  )
  expect(response.status()).toBe(200)
  const actual = seriesFiresSchema.parse(await response.json())
  expect(actual.items).toHaveLength(60)
  expect(new Set(actual.items.map((fire) => fire.fireAt)).size).toBe(60)
  expect(
    actual.items.every(
      (fire) =>
        fire.outcome === "Skipped" &&
        fire.reason === "overlap" &&
        fire.runId === null
    )
  ).toBe(true)
  await page.getByRole("button", { name: "Dark theme" }).click()
  await page.setViewportSize({ width: 375, height: 812 })
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth
    )
  ).toBe(true)
  await accessible(page)
  await page.reload()
  await expect(table.getByRole("row")).toHaveCount(51)
})

test("actual scheduler overlap creates a visible skip without a run", async ({
  page,
  request,
}) => {
  const created = await request.post("/api/v1/schedules", {
    data: {
      workflowId: "ui-tests/release/workflow",
      name: `Live history ${randomUUID()}`,
      timezone: "Etc/UTC",
      interval: "00:00:01",
      overlap: "Skip",
    },
  })
  expect(created.status()).toBe(201)
  const schedule = scheduleSchema.parse(parseJson(await created.text()))
  const etag = created.headers().etag
  if (!etag) {
    throw new Error("The created history schedule has no ETag.")
  }
  try {
    await expect
      .poll(async () => {
        const response = await request.get(
          `/api/v1/series/${schedule.seriesId}/fires`
        )
        expect(response.status()).toBe(200)
        const page = seriesFiresSchema.parse(await response.json())
        return (
          page.items.some(
            (fire) =>
              fire.outcome === "Skipped" &&
              fire.reason === "overlap" &&
              fire.runId === null
          ) &&
          page.items.some(
            (fire) => fire.outcome === "Started" && fire.runId !== null
          )
        )
      })
      .toBe(true)
    await page.goto(`/series/${schedule.seriesId}`)
    await expect(
      page
        .getByRole("table")
        .getByRole("row")
        .filter({ hasText: "Skipped" })
        .first()
    ).toContainText("overlap")
    await expect(
      page.getByRole("table").getByRole("row").filter({ hasText: "Started" })
    ).toHaveCount(1)
    await accessible(page)
  } finally {
    const deleted = await request.delete(`/api/v1/schedules/${schedule.id}`, {
      headers: { "If-Match": etag },
    })
    expect(deleted.status()).toBe(204)
  }
  await page.reload()
  await expect(page.getByRole("table")).toBeVisible()
})
