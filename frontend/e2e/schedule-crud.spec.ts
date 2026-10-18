import { randomUUID } from "node:crypto"
import AxeBuilder from "@axe-core/playwright"
import { expect, test, type Page } from "@playwright/test"
import { scheduleSchema, type Schedule } from "../src/lib/api/client"
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

async function create(page: Page): Promise<{ id: string; name: string }> {
  const name = `Browser ${randomUUID()}`
  await page.goto("/schedules/new?workflow=ui-tests%2Frelease%2Fworkflow")
  await page.getByLabel("Schedule name", { exact: true }).fill(name)
  await page
    .getByLabel("Timezone", { exact: true })
    .selectOption("America/New_York")
  await page.getByRole("radio", { name: "Interval", exact: true }).click()
  await page.getByLabel("Interval seconds", { exact: true }).fill("3600")
  await expect(page.locator("section ol li")).toHaveCount(5)
  await expect(
    page.getByRole("button", { name: "Create schedule", exact: true })
  ).toBeEnabled()
  await page
    .getByRole("button", { name: "Create schedule", exact: true })
    .click()
  await expect(page).toHaveURL(/\/schedules\/[a-f0-9-]{36}$/)
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(name)
  const id = new URL(page.url()).pathname.split("/").at(-1)
  if (!id) {
    throw new Error("The UI did not navigate to its created schedule.")
  }
  return { id, name }
}

function requestFor(schedule: Schedule) {
  return {
    workflowId: schedule.workflowId,
    name: schedule.name,
    timezone: schedule.timezone,
    cron: schedule.cron,
    interval: schedule.interval,
    versionPin: schedule.versionPin,
    variables: schedule.variables,
    overlap: schedule.overlap,
    catchUp: schedule.catchUp,
  }
}

test("create, edit, pause, resume and delete through the actual control plane", async ({
  page,
  request,
}) => {
  await page.goto("/schedules/new?workflow=ui-tests%2Frelease%2Fworkflow")
  await accessible(page)
  const { id } = await create(page)
  const original = await request.get(`/api/v1/schedules/${id}`)
  expect(original.status()).toBe(200)
  expect(original.headers().etag).toBe('"1"')
  const originalText = await original.text()
  expect(originalText).toContain('"sequence":9223372036854775807')
  const originalSchedule = scheduleSchema.parse(parseJson(originalText))
  await expect(page.getByLabel("Variable overrides (JSON object)")).toHaveValue(
    /9223372036854775807/
  )
  await expect(page.getByLabel("Workflow identity")).toHaveAttribute(
    "readonly",
    ""
  )
  await page
    .getByLabel("Schedule name", { exact: true })
    .fill("Edited browser schedule")
  await expect(page.getByRole("button", { name: "Save changes" })).toBeEnabled()
  await page.getByRole("button", { name: "Save changes" }).click()
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    "Edited browser schedule"
  )
  expect(await (await request.get(`/api/v1/schedules/${id}`)).text()).toContain(
    '"sequence":9223372036854775807'
  )
  await accessible(page)

  await page
    .getByLabel("Schedule name", { exact: true })
    .fill("Unsaved edits survive pause")
  await page
    .getByRole("button", { name: "Pause schedule", exact: true })
    .click()
  await expect(
    page.getByRole("button", { name: "Resume schedule", exact: true })
  ).toBeVisible()
  await expect(page.getByLabel("Schedule name", { exact: true })).toHaveValue(
    "Unsaved edits survive pause"
  )
  const paused = scheduleSchema.parse(
    parseJson(await (await request.get(`/api/v1/schedules/${id}`)).text())
  )
  expect(paused.paused).toBe(true)
  expect(paused.nextFireAt).toBeNull()
  expect(paused.seriesId).toBe(originalSchedule.seriesId)
  await page
    .getByRole("button", { name: "Resume schedule", exact: true })
    .click()
  await expect(
    page.getByRole("button", { name: "Pause schedule", exact: true })
  ).toBeVisible()
  await expect(page.getByLabel("Schedule name", { exact: true })).toHaveValue(
    "Unsaved edits survive pause"
  )
  await expect(page.getByRole("button", { name: "Save changes" })).toBeEnabled()
  await page.getByRole("button", { name: "Save changes" }).click()
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    "Unsaved edits survive pause"
  )

  await page.getByRole("button", { name: "Dark theme" }).click()
  await accessible(page)
  await page.setViewportSize({ width: 375, height: 812 })
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth
    )
  ).toBe(true)
  await accessible(page)
  await page
    .getByRole("button", { name: "Delete schedule", exact: true })
    .click()
  await expect(page.getByRole("alertdialog")).toBeVisible()
  await accessible(page)
  await page.keyboard.press("Escape")
  await expect(page.getByRole("alertdialog")).toBeHidden()
  await expect(
    page.getByRole("button", { name: "Delete schedule", exact: true })
  ).toBeFocused()
  await page
    .getByRole("button", { name: "Delete schedule", exact: true })
    .click()
  await page.getByRole("button", { name: "Confirm delete" }).click()
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    "Schedule deleted"
  )
  expect((await request.get(`/api/v1/schedules/${id}`)).status()).toBe(404)
})

test("invalid JSON, intervals and server-validated cron keep submit disabled", async ({
  page,
}) => {
  await page.goto("/schedules/new?workflow=ui-tests%2Frelease%2Fworkflow")
  const submit = page.getByRole("button", {
    name: "Create schedule",
    exact: true,
  })
  await expect(submit).toBeDisabled()
  await page
    .getByLabel("Schedule name", { exact: true })
    .fill("Invalid boundary checks")
  await page.getByLabel("Variable overrides (JSON object)").fill("[]")
  await expect(
    page.getByLabel("Variable overrides (JSON object)")
  ).toHaveAttribute("aria-invalid", "true")
  await expect(submit).toBeDisabled()
  await page.getByLabel("Variable overrides (JSON object)").fill("{}")
  await page.getByLabel("Cron expression").fill("61 * * * *")
  await expect(
    page.getByRole("alert").filter({ hasText: "Schedule validation failed" })
  ).toBeVisible()
  await expect(submit).toBeDisabled()
  await page.getByRole("radio", { name: "Interval", exact: true }).click()
  await page.getByLabel("Interval seconds", { exact: true }).fill("1.5")
  await expect(
    page.getByLabel("Interval seconds", { exact: true })
  ).toHaveAttribute("aria-invalid", "true")
  await expect(submit).toBeDisabled()
  await accessible(page)
  await page.getByLabel("Interval seconds", { exact: true }).fill("3600")
  await expect(page.locator("section ol li")).toHaveCount(5)
  await expect(submit).toBeEnabled()
})

test("real 412 preserves unsaved edits until explicit current-version reload", async ({
  page,
  request,
}) => {
  const { id } = await create(page)
  const current = await request.get(`/api/v1/schedules/${id}`)
  const etag = current.headers().etag
  if (!etag) {
    throw new Error("The API did not return a strong ETag.")
  }
  const updated = await request.put(`/api/v1/schedules/${id}`, {
    headers: { "If-Match": etag },
    data: {
      ...requestFor(scheduleSchema.parse(parseJson(await current.text()))),
      name: "Changed by another operator",
    },
  })
  expect(updated.status()).toBe(200)
  await page
    .getByLabel("Schedule name", { exact: true })
    .fill("My unsaved change")
  await expect(page.getByRole("button", { name: "Save changes" })).toBeEnabled()
  await page.getByRole("button", { name: "Save changes" }).click()
  await expect(
    page
      .getByRole("alert")
      .filter({ hasText: "This schedule changed elsewhere" })
  ).toBeVisible()
  await expect(page.getByLabel("Schedule name", { exact: true })).toHaveValue(
    "My unsaved change"
  )
  await expect(
    page.getByRole("button", { name: "Save changes" })
  ).toBeDisabled()
  await page.getByRole("button", { name: "Reload latest schedule" }).click()
  await expect(page.getByLabel("Schedule name", { exact: true })).toHaveValue(
    "Changed by another operator"
  )
  await page
    .getByLabel("Schedule name", { exact: true })
    .fill("Saved after reload")
  await expect(page.getByRole("button", { name: "Save changes" })).toBeEnabled()
  await page.getByRole("button", { name: "Save changes" }).click()
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    "Saved after reload"
  )
})

test("Int64 overrides survive preview, write, response validation and reload exactly", async ({
  page,
  request,
}) => {
  const { id } = await create(page)
  await page
    .getByLabel("Variable overrides (JSON object)")
    .fill('{"sequence":-9223372036854775808}')
  await expect(page.locator("section ol li")).toHaveCount(5)
  await expect(page.getByRole("button", { name: "Save changes" })).toBeEnabled()
  await Promise.all([
    page.waitForResponse(
      (response) =>
        response.request().method() === "PUT" &&
        response.url().endsWith(`/api/v1/schedules/${id}`) &&
        response.status() === 200
    ),
    page.getByRole("button", { name: "Save changes" }).click(),
  ])
  const persisted = await request.get(`/api/v1/schedules/${id}`)
  expect(await persisted.text()).toContain('"sequence":-9223372036854775808')
  await page.reload()
  await expect(page.getByLabel("Variable overrides (JSON object)")).toHaveValue(
    /-9223372036854775808/
  )
})

test("network failures surface explicitly and cannot reuse a previous preview", async ({
  page,
  context,
}) => {
  await page.goto("/schedules/new?workflow=ui-tests%2Frelease%2Fworkflow")
  await page.getByLabel("Schedule name", { exact: true }).fill("Before offline")
  await expect(page.locator("section ol li")).toHaveCount(5)
  await context.setOffline(true)
  await page.getByLabel("Schedule name", { exact: true }).fill("Offline")
  await expect(
    page.getByRole("alert").filter({ hasText: "Request failed" })
  ).toBeVisible()
  await expect(
    page.getByRole("button", { name: "Create schedule", exact: true })
  ).toBeDisabled()
  await context.setOffline(false)
  await page.getByLabel("Schedule name", { exact: true }).fill("Online again")
  await expect(page.locator("section ol li")).toHaveCount(5)
  await expect(
    page.getByRole("button", { name: "Create schedule", exact: true })
  ).toBeEnabled()
})
