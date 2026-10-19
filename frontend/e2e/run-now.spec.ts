import { randomUUID } from "node:crypto"
import { readFile } from "node:fs/promises"
import AxeBuilder from "@axe-core/playwright"
import { expect, test } from "@playwright/test"
import { z } from "zod"
import { scheduleSchema, seriesFiresSchema } from "../src/lib/api/client"
import { parseJson } from "../src/lib/json"

test("Run now from paused schedule and series history creates only same-series Manual runs", async ({
  page,
  request,
}) => {
  const created = await request.post("/api/v1/schedules", {
    data: {
      workflowId: "ui-tests/release/workflow",
      name: `Manual browser ${randomUUID()}`,
      timezone: "Etc/UTC",
      interval: "01:00:00",
    },
  })
  expect(created.status()).toBe(201)
  const schedule = scheduleSchema.parse(parseJson(await created.text()))
  const paused = await request.post(`/api/v1/schedules/${schedule.id}/pause`, {
    headers: { "If-Match": created.headers().etag! },
  })
  expect(paused.status()).toBe(202)
  const before = await (
    await request.get(`/api/v1/schedules/${schedule.id}`)
  ).text()
  const pausedEtag = paused.headers().etag
  if (!pausedEtag) throw new Error("Paused schedule has no ETag.")
  const submissions: { body: string | null; key: string | undefined }[] = []
  page.on("request", (request) => {
    if (
      request.method() === "POST" &&
      new URL(request.url()).pathname ===
        `/api/v1/schedules/${schedule.id}/run-now`
    )
      submissions.push({
        body: request.postData(),
        key: request.headers()["idempotency-key"],
      })
  })
  const runIds: string[] = []
  let removed = false
  try {
    await page.goto(`/schedules/${schedule.id}`)
    await page
      .getByLabel("Schedule name", { exact: true })
      .fill("Keep this unsaved draft")
    const firstAccepted = page.waitForResponse(
      (response) =>
        new URL(response.url()).pathname.endsWith(`/${schedule.id}/run-now`) &&
        response.request().method() === "POST"
    )
    await page.getByRole("button", { name: "Run now", exact: true }).focus()
    await page.keyboard.press("Enter")
    const first = await firstAccepted
    expect(first.status()).toBe(202)
    runIds.push(z.object({ runId: z.uuid() }).parse(await first.json()).runId)
    await expect(
      page.getByRole("link", { name: runIds[0], exact: true })
    ).toBeVisible()
    await expect(page.getByText("Run accepted in this series.")).toContainText(
      "Manual"
    )
    await expect(page.getByLabel("Schedule name", { exact: true })).toHaveValue(
      "Keep this unsaved draft"
    )
    await expect(
      page.getByRole("button", { name: "Resume schedule", exact: true })
    ).toBeVisible()
    await page
      .getByRole("link", { name: "View series history", exact: true })
      .click()
    await expect(page).toHaveURL(`/series/${schedule.seriesId}`)
    const secondAccepted = page.waitForResponse(
      (response) =>
        new URL(response.url()).pathname.endsWith(`/${schedule.id}/run-now`) &&
        response.request().method() === "POST"
    )
    await page.getByRole("button", { name: "Run now", exact: true }).focus()
    await page.keyboard.press("Enter")
    const second = await secondAccepted
    expect(second.status()).toBe(202)
    runIds.push(z.object({ runId: z.uuid() }).parse(await second.json()).runId)
    expect(new Set(runIds).size).toBe(2)
    expect(submissions).toHaveLength(2)
    expect(
      submissions.every(
        (submission) =>
          submission.body === null && z.uuid().safeParse(submission.key).success
      )
    ).toBe(true)
    expect(submissions[0]!.key).not.toBe(submissions[1]!.key)
    const replayed = await request.post(
      `/api/v1/schedules/${schedule.id}/run-now`,
      {
        headers: { "Idempotency-Key": submissions[1]!.key! },
      }
    )
    expect(replayed.status()).toBe(202)
    expect(
      z.object({ runId: z.uuid() }).parse(await replayed.json()).runId
    ).toBe(runIds[1])
    await expect
      .poll(async () => {
        const history = await request.get(
          `/api/v1/series/${schedule.seriesId}/history`
        )
        expect(history.status()).toBe(200)
        return z
          .object({
            currentScheduleId: z.uuid().nullable(),
            items: z.array(
              z.object({
                runId: z.uuid().nullable(),
                seriesId: z.uuid().nullable(),
                trigger: z.string().nullable(),
              })
            ),
          })
          .parse(await history.json())
      })
      .toMatchObject({
        currentScheduleId: schedule.id,
        items: [
          { runId: runIds[1], seriesId: schedule.seriesId, trigger: "Manual" },
          { runId: runIds[0], seriesId: schedule.seriesId, trigger: "Manual" },
        ],
      })
    await page.getByRole("button", { name: "Refresh run history" }).click()
    const table = page.getByRole("table")
    await expect(table.getByRole("row")).toHaveCount(3)
    for (const id of runIds) {
      const row = table.getByRole("row").filter({ hasText: id! })
      await expect(row).toContainText("Manual")
      await expect(row).not.toContainText("sequence (Long)")
    }
    expect(
      await (await request.get(`/api/v1/schedules/${schedule.id}`)).text()
    ).toBe(before)
    const fires = await request.get(`/api/v1/series/${schedule.seriesId}/fires`)
    expect(seriesFiresSchema.parse(await fires.json()).items).toEqual([])
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
    const deleted = await request.delete(`/api/v1/schedules/${schedule.id}`, {
      headers: { "If-Match": pausedEtag },
    })
    expect(deleted.status()).toBe(204)
    removed = true
    await page.reload()
    await expect(
      page.getByText(
        "Run now unavailable: this series has no current schedule."
      )
    ).toBeVisible()
    await expect(
      page.getByRole("button", { name: "Run now", exact: true })
    ).toHaveCount(0)
    await expect(table.getByRole("row")).toHaveCount(3)
  } finally {
    if (!removed) {
      const deleted = await request.delete(`/api/v1/schedules/${schedule.id}`, {
        headers: { "If-Match": pausedEtag },
      })
      expect(deleted.status()).toBe(204)
    }
  }
})

test("code-origin schedules remain read-only but permit Operator Run now", async ({
  page,
  request,
}) => {
  const fixture = z
    .object({ codeScheduleId: z.uuid() })
    .parse(
      JSON.parse(
        await readFile(new URL("../.test-stack.json", import.meta.url), "utf8")
      )
    )
  const before = await request.get(
    `/api/v1/schedules/${fixture.codeScheduleId}`
  )
  expect(before.status()).toBe(200)
  const original = await before.text()
  const schedule = scheduleSchema.parse(parseJson(original))
  await page.goto(`/schedules/${schedule.id}`)
  await expect(page.getByText("Code schedule", { exact: true })).toBeVisible()
  await expect(
    page.getByRole("button", { name: "Delete schedule", exact: true })
  ).toHaveCount(0)
  const accepted = page.waitForResponse((response) =>
    new URL(response.url()).pathname.endsWith(`/${schedule.id}/run-now`)
  )
  await page.getByRole("button", { name: "Run now", exact: true }).click()
  const response = await accepted
  expect(response.status()).toBe(202)
  const { runId } = z.object({ runId: z.uuid() }).parse(await response.json())
  await expect(
    page.getByRole("link", { name: runId, exact: true })
  ).toBeVisible()
  expect(
    await (await request.get(`/api/v1/schedules/${schedule.id}`)).text()
  ).toBe(original)
  await expect
    .poll(async () => {
      const history = await request.get(
        `/api/v1/series/${schedule.seriesId}/history`
      )
      expect(history.status()).toBe(200)
      return z
        .object({
          items: z.array(
            z.object({
              runId: z.uuid().nullable(),
              trigger: z.string().nullable(),
            })
          ),
        })
        .parse(await history.json())
        .items.some((row) => row.runId === runId && row.trigger === "Manual")
    })
    .toBe(true)
})
