import { readFile } from "node:fs/promises"
import { randomUUID } from "node:crypto"
import AxeBuilder from "@axe-core/playwright"
import { expect, test, type Page } from "@playwright/test"
import { z } from "zod"
import { scheduleSchema, seriesFiresSchema } from "../src/lib/api/client"
import { parseJson } from "../src/lib/json"
import { expectInstant, instantTicks } from "./instants"

test("actual catch-up run is badged while its retained source remains Missed", async ({
  page,
  request,
}) => {
  const fixture = z
    .object({ catchUpSeriesId: z.uuid() })
    .parse(
      JSON.parse(
        await readFile(new URL("../.test-stack.json", import.meta.url), "utf8")
      )
    )
  await expect
    .poll(async () => {
      const response = await request.get(
        `/api/v1/series/${fixture.catchUpSeriesId}/fires`
      )
      expect(response.status()).toBe(200)
      const history = seriesFiresSchema.parse(await response.json())
      return (
        history.items.filter((fire) => fire.outcome === "Missed").length ===
          10 &&
        history.items.filter(
          (fire) =>
            fire.outcome === "Missed" &&
            fire.reason === "catch-up" &&
            fire.runId !== null
        ).length === 1
      )
    })
    .toBe(true)
  await page.goto(`/series/${fixture.catchUpSeriesId}`)
  await expect(page.getByText("Catch-up", { exact: true })).toHaveCount(1)
  const badge = page.getByText("Catch-up", { exact: true })
  await expect(badge).toHaveAttribute(
    "title",
    "Recovery run for the latest missed scheduled fire"
  )
  await expect(page.getByRole("row").filter({ has: badge })).toContainText(
    "Schedule missed"
  )
  await accessible(page)
  await page.getByRole("button", { name: "Dark theme" }).click()
  await page.setViewportSize({ width: 375, height: 812 })
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth
    )
  ).toBe(true)
  await accessible(page)
})

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
  await expect(table.getByRole("row").nth(1)).toContainText("Schedule skipped")
  await expect(table.getByRole("row").nth(1)).toContainText("overlap")
  await expect(table.getByRole("row").nth(1)).toContainText("No run")
  await accessible(page)
  await page.getByRole("button", { name: "Load more history" }).click()
  await expect(table.getByRole("row")).toHaveCount(61)
  await expect(
    page.getByRole("button", { name: "Load more history" })
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
        .filter({ hasText: "Schedule skipped" })
        .first()
    ).toContainText("overlap")
    await expect(
      page
        .getByRole("table")
        .getByRole("row")
        .filter({ hasText: "Schedule triggered" })
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

test("workflow and series history show actual committed abort attribution and restore typed URL filters", async ({
  page,
  request,
}) => {
  const workflowId = "ui-tests/release/workflow"
  const created = await request.post("/api/v1/schedules", {
    data: {
      workflowId,
      name: `Abort history ${randomUUID()}`,
      timezone: "Etc/UTC",
      interval: "01:00:00",
    },
  })
  expect(created.status()).toBe(201)
  const schedule = scheduleSchema.parse(parseJson(await created.text()))
  const etag = created.headers().etag
  if (!etag) throw new Error("History fixture schedule has no ETag.")
  try {
    const accepted = await request.post(
      `/api/v1/schedules/${schedule.id}/run-now`
    )
    expect(accepted.status()).toBe(202)
    const { runId } = z.object({ runId: z.uuid() }).parse(await accepted.json())
    await expect
      .poll(async () => {
        const result = await request.get(
          `/api/v1/series/${schedule.seriesId}/history`
        )
        expect(result.status()).toBe(200)
        return z
          .object({ items: z.array(z.object({ runId: z.uuid().nullable() })) })
          .parse(await result.json())
          .items.some((row) => row.runId === runId)
      })
      .toBe(true)
    const reason = `Browser history ${randomUUID()}`
    const aborted = await request.post(`/api/v1/runs/${runId}/abort`, {
      data: { reason, source: "UI" },
    })
    expect(aborted.status()).toBe(202)
    const receipt = z
      .object({
        by: z.string(),
        byName: z.string(),
        at: z.string(),
        reason: z.string(),
      })
      .parse(await aborted.json())
    let versionId = ""
    let version = ""
    let recordedAt = ""
    await expect
      .poll(async () => {
        const result = await request.get(
          `/api/v1/series/${schedule.seriesId}/history`,
          {
            params: { status: "Aborted", abortedBy: receipt.by },
          }
        )
        expect(result.status()).toBe(200)
        const text = await result.text()
        expect(text).not.toContain("9223372036854775807")
        const history = z
          .object({
            items: z.array(
              z.object({
                runId: z.uuid().nullable(),
                versionId: z.uuid().nullable(),
                version: z.string().nullable(),
                trigger: z.string().nullable(),
                abort: z
                  .object({
                    reason: z.string(),
                    at: z.string(),
                    by: z.string(),
                  })
                  .nullable(),
              })
            ),
          })
          .parse(JSON.parse(text))
        const row = history.items.find((item) => item.runId === runId)
        versionId = row?.versionId ?? ""
        version = row?.version ?? ""
        recordedAt = row?.abort?.at ?? ""
        return (
          row?.abort?.reason === reason &&
          row.abort.by === receipt.by &&
          instantTicks(row.abort.at) === instantTicks(receipt.at) &&
          row.trigger === "Manual"
        )
      })
      .toBe(true)
    await page.goto(`/series/${schedule.seriesId}`)
    const form = page.getByRole("form", { name: "History filters" })
    await form.getByLabel("Run status").selectOption("Aborted")
    await form.getByLabel("Cancelled by (principal UUID)").fill(receipt.by)
    await form.getByLabel("History order").selectOption("createdAtAsc")
    await form
      .getByRole("button", { name: "Filter history", exact: true })
      .focus()
    await page.keyboard.press("Enter")
    const copied = page.url()
    const table = page.getByRole("table")
    await expect(table).toContainText(reason)
    await expect(table).toContainText("Aborted")
    await expect(table).toContainText("Manual")
    await expect(table).not.toContainText("sequence (Long)")
    await expect(
      table.getByRole("columnheader", { name: /Variables/ })
    ).toHaveCount(0)
    await expect(table).not.toContainText("9223372036854775807")
    const row = table.getByRole("row").filter({ hasText: runId })
    await expect(row).toContainText(receipt.by)
    await expectInstant(
      row.locator(`time[datetime="${recordedAt}"]`),
      receipt.at
    )
    await expect(
      row.getByRole("link", { name: version, exact: true })
    ).toHaveAttribute(
      "href",
      `/t/ui-tests/release/w/workflow/versions/${versionId}`
    )
    await page.goto(copied)
    await expect(form.getByLabel("Run status")).toHaveValue("Aborted")
    await expect(form.getByLabel("Cancelled by (principal UUID)")).toHaveValue(
      receipt.by
    )
    await expect(form.getByLabel("History order")).toHaveValue("createdAtAsc")
    await expect(table).toContainText(reason)
    await accessible(page)
    await page.getByRole("button", { name: "Dark theme" }).click()
    await page.setViewportSize({ width: 375, height: 812 })
    await accessible(page)
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth
      )
    ).toBe(true)
    await page.goto(
      `/t/ui-tests/release/w/workflow/history?status=Aborted&abortedBy=${receipt.by}`
    )
    await expect(
      page.getByRole("heading", { name: "Workflow history", exact: true })
    ).toBeVisible()
    await expect(page.getByRole("table")).toContainText(reason)
    await page.getByRole("link", { name: version, exact: true }).first().focus()
    await page.keyboard.press("Enter")
    await expect(
      page.getByRole("heading", { name: "Task Plan", exact: true })
    ).toBeVisible()
    await expect(page).toHaveURL(new RegExp(`/versions/${versionId}$`))
  } finally {
    const deleted = await request.delete(`/api/v1/schedules/${schedule.id}`, {
      headers: { "If-Match": etag },
    })
    expect(deleted.status()).toBe(204)
  }
})
