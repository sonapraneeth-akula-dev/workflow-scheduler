import { randomUUID } from "node:crypto"
import AxeBuilder from "@axe-core/playwright"
import { expect, test } from "@playwright/test"
import { z } from "zod"
import { scheduleSchema } from "../src/lib/api/client"
import { parseJson } from "../src/lib/json"

const workflow =
  process.env.WF_RECURRING_WORKFLOW ?? "ui-tests/release/workflow"
const [team, type] = workflow.split("/")
if (!team || !type) throw new Error(`Invalid recurring workflow: ${workflow}.`)
const filteredUrl = `/recurring-jobs?${new URLSearchParams({ team, type })}`

test("navbar opens grouped recurring jobs and team/type filters survive reload", async ({
  page,
  request,
}) => {
  const response = await request.get(
    `/api/v1/schedules?${new URLSearchParams({ team, type, limit: "50" })}`
  )
  expect(response.status()).toBe(200)
  const jobs = z
    .object({ items: z.array(scheduleSchema) })
    .parse(parseJson(await response.text())).items
  expect(jobs.length).toBeGreaterThan(0)
  await page.goto("/")
  const link = page
    .getByRole("navigation", { name: "Main navigation" })
    .getByRole("link", { name: "Recurring jobs", exact: true })
  await expect(link).toHaveAttribute("href", "/recurring-jobs")
  await expect(link).not.toHaveAttribute("target", "_blank")
  await link.click()
  await expect(page).toHaveURL("/recurring-jobs")
  await expect(
    page.getByRole("heading", { name: "Recurring jobs", exact: true })
  ).toBeVisible()
  await expect(
    page.getByRole("combobox", { name: "Workflow type", exact: true })
  ).toBeDisabled()
  await page
    .getByRole("combobox", { name: "Team", exact: true })
    .selectOption(team)
  const types = page.getByRole("combobox", {
    name: "Workflow type",
    exact: true,
  })
  await expect(types).toBeEnabled()
  await types.selectOption(type)
  await expect(page).toHaveURL(filteredUrl)
  await page.reload()
  await expect(types).toHaveValue(type)
  await expect(
    page.getByRole("combobox", { name: "Team", exact: true })
  ).toHaveValue(team)
  for (const job of jobs) {
    const card = page.locator(`[data-schedule-id="${job.id}"]`)
    await expect(card).toBeVisible()
    await expect(
      card.getByRole("heading", { name: job.name, exact: true })
    ).toBeVisible()
    await expect(
      card.getByRole("link", { name: "Schedule details" })
    ).toHaveAttribute("href", `/schedules/${job.id}`)
    await expect(
      card.getByRole("link", { name: "Series history" })
    ).toHaveAttribute("href", `/series/${job.seriesId}`)
    await expect(card).toContainText(job.workflowId)
    expect(await card.locator("xpath=ancestor::section").count()).toBe(2)
  }
  await expect(page.getByRole("alert")).toHaveCount(0)
})

test("Run now from a paused grouped card persists a Manual run without changing saved schedule settings", async ({
  page,
  request,
}) => {
  const sourceResponse = await request.get(
    `/api/v1/schedules?${new URLSearchParams({ workflowId: workflow, limit: "1" })}`
  )
  expect(sourceResponse.status()).toBe(200)
  const [source] = z
    .object({ items: z.array(scheduleSchema) })
    .parse(parseJson(await sourceResponse.text())).items
  if (!source)
    throw new Error(`No existing recurring schedule for ${workflow}.`)
  const created = await request.post("/api/v1/schedules", {
    data: JSON.stringify({
      workflowId: workflow,
      name: `Recurring jobs browser ${randomUUID()}`,
      timezone: source.timezone,
      interval: "01:00:00",
      versionPin: source.versionPin,
      variables: source.variables,
    }),
    headers: { "Content-Type": "application/json" },
  })
  expect(created.status()).toBe(201)
  const schedule = scheduleSchema.parse(parseJson(await created.text()))
  let etag = created.headers().etag
  if (!etag) throw new Error("Created recurring schedule has no ETag.")
  try {
    const paused = await request.post(
      `/api/v1/schedules/${schedule.id}/pause`,
      {
        headers: { "If-Match": etag },
      }
    )
    expect(paused.status()).toBe(202)
    etag = paused.headers().etag
    if (!etag) throw new Error("Paused recurring schedule has no ETag.")
    const beforeResponse = await request.get(`/api/v1/schedules/${schedule.id}`)
    expect(beforeResponse.status()).toBe(200)
    const before = await beforeResponse.text()
    expect(scheduleSchema.parse(parseJson(before)).paused).toBe(true)
    await page.goto(filteredUrl)
    const card = page.locator(`[data-schedule-id="${schedule.id}"]`)
    await expect(card.getByText("Paused", { exact: true })).toBeVisible()
    const accepted = page.waitForResponse(
      (response) =>
        new URL(response.url()).pathname ===
          `/api/v1/schedules/${schedule.id}/run-now` &&
        response.request().method() === "POST"
    )
    await card.getByRole("button", { name: "Run now", exact: true }).focus()
    await page.keyboard.press("Enter")
    const response = await accepted
    expect(response.status()).toBe(202)
    expect(response.request().postData()).toBeNull()
    expect(
      z.uuid().safeParse(response.request().headers()["idempotency-key"])
        .success
    ).toBe(true)
    const { runId } = z.object({ runId: z.uuid() }).parse(await response.json())
    await expect(card.getByRole("status")).toContainText(
      "Run accepted in this series"
    )
    await expect(card.getByRole("status")).toContainText(
      "next fire and saved variables are unchanged"
    )
    await expect(
      card.getByRole("link", { name: runId, exact: true })
    ).toHaveAttribute("href", `/runs/${runId}`)
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
        items: [{ runId, seriesId: schedule.seriesId, trigger: "Manual" }],
      })
    const after = await request.get(`/api/v1/schedules/${schedule.id}`)
    expect(after.status()).toBe(200)
    expect(await after.text()).toBe(before)
    expect(after.headers().etag).toBe(etag)
    await expect(card.getByText("Paused; no automatic fires")).toBeVisible()
    await expect(
      card.getByRole("button", { name: /resume|pause|delete|save/i })
    ).toHaveCount(0)
  } finally {
    const deleted = await request.delete(`/api/v1/schedules/${schedule.id}`, {
      headers: { "If-Match": etag! },
    })
    expect(deleted.status()).toBe(204)
  }
})

test("grouped recurring jobs and navbar remain accessible without overflow or overlap across themes and widths", async ({
  page,
}, info) => {
  test.setTimeout(120_000)
  const errors: string[] = []
  page.on("pageerror", (error) => errors.push(error.message))
  page.on("console", (message) => {
    if (message.type() === "error") errors.push(message.text())
  })
  for (const colorScheme of ["light", "dark"] as const) {
    await page.emulateMedia({ colorScheme })
    for (const width of [390, 1024, 1440]) {
      await page.setViewportSize({ width, height: 1000 })
      await page.goto(filteredUrl)
      await expect(page.locator("[data-schedule-id]").first()).toBeVisible()
      await expect(page.getByRole("alert")).toHaveCount(0)
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth)
      ).toBeLessThanOrEqual(width)
      const overlap = await page.locator("header").evaluate((header) => {
        const elements = [
          header.querySelector('a[aria-label="Kardia home"]'),
          header.querySelector("nav"),
          header.querySelector("nav")?.nextElementSibling,
        ]
        const rectangles = elements.map((element) => {
          if (!element) throw new Error("Missing navbar layout element.")
          return element.getBoundingClientRect()
        })
        return rectangles.some((left, index) =>
          rectangles
            .slice(index + 1)
            .some(
              (right) =>
                left.left < right.right &&
                right.left < left.right &&
                left.top < right.bottom &&
                right.top < left.bottom
            )
        )
      })
      expect(overlap).toBe(false)
      const audit = await new AxeBuilder({ page })
        .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
        .analyze()
      expect(
        audit.violations.map(({ id, nodes }) => ({
          id,
          nodes: nodes.map(({ target, failureSummary }) => ({
            target,
            failureSummary,
          })),
        }))
      ).toEqual([])
      await page.screenshot({
        path: info.outputPath(`recurring-jobs-${colorScheme}-${width}.png`),
        fullPage: true,
      })
    }
  }
  expect(errors).toEqual([])
})
