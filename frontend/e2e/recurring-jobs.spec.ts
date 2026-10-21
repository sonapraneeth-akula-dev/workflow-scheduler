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

test("navbar dropdown opens all teams, team and type job tables and filters survive reload", async ({
  page,
  request,
}) => {
  test.setTimeout(60_000)
  const response = await request.get(
    `/api/v1/schedules?${new URLSearchParams({ team, type, limit: "50" })}`
  )
  expect(response.status()).toBe(200)
  const jobs = z
    .object({ items: z.array(scheduleSchema) })
    .parse(parseJson(await response.text())).items
  expect(jobs.length).toBeGreaterThan(0)
  await page.goto("/")
  const trigger = page
    .getByRole("navigation", { name: "Main navigation" })
    .getByRole("button", { name: "Recurring jobs menu", exact: true })
  await trigger.focus()
  await page.keyboard.press("Enter")
  const menu = page.getByRole("menu", {
    name: "Recurring jobs teams",
    exact: true,
  })
  const link = menu.getByRole("menuitem", { name: "All teams", exact: true })
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
  await trigger.click()
  const teamItem = menu.locator(
    `[role="menuitem"][aria-haspopup="menu"][data-team="${team}"]`
  )
  await expect(teamItem).toBeVisible()
  const teamName = (await teamItem.textContent())!.trim()
  await teamItem.focus()
  await page.keyboard.press("ArrowRight")
  const submenu = page.getByRole("menu", {
    name: `${team} workflow types`,
    exact: true,
  })
  const allTypes = submenu.getByRole("menuitem", {
    name: "All workflow types",
    exact: true,
  })
  await expect(allTypes).toHaveAttribute(
    "href",
    `/recurring-jobs?${new URLSearchParams({ team })}`
  )
  await allTypes.click()
  await expect(
    page.getByRole("heading", {
      name: `${teamName} Recurring jobs`,
      level: 1,
      exact: true,
    })
  ).toBeVisible()
  const types = page.getByRole("combobox", {
    name: "Workflow type",
    exact: true,
  })
  await expect(types).toBeEnabled()
  await expect(types).toHaveValue("")
  await trigger.click()
  await teamItem.focus()
  await page.keyboard.press("ArrowRight")
  const typeLink = submenu.locator(
    `[role="menuitem"][href*="type=${encodeURIComponent(type)}"]`
  )
  await expect(typeLink).toHaveAttribute("href", filteredUrl)
  await expect(typeLink).not.toHaveAttribute("target", "_blank")
  await typeLink.focus()
  await page.keyboard.press("Enter")
  await expect(page).toHaveURL(filteredUrl)
  await expect(trigger).toHaveClass(/bg-secondary/)
  await expect(
    page.getByRole("button", { name: "Service history menu", exact: true })
  ).not.toHaveClass(/bg-secondary/)
  await page.reload()
  await expect(types).toHaveValue(type)
  await expect(
    page.getByRole("combobox", { name: "Team", exact: true })
  ).toHaveValue(team)
  for (const job of jobs) {
    const row = page.locator(`tr[data-schedule-id="${job.id}"]`)
    await expect(row).toBeVisible()
    await expect(
      row.getByRole("rowheader", { name: job.name, exact: true })
    ).toBeVisible()
    await expect(
      row.getByRole("link", { name: "Schedule details" })
    ).toHaveAttribute("href", `/schedules/${job.id}`)
    await expect(
      row.getByRole("link", { name: "Series history" })
    ).toHaveAttribute("href", `/series/${job.seriesId}`)
    const group = row.locator("..")
    await expect(group).toHaveAttribute("data-workflow-id", job.workflowId)
    await expect(group).toContainText(job.workflowId)
    await expect(group.getByRole("heading", { level: 4 })).toBeVisible()
    const header = group.locator(":scope > tr").first()
    await expect(header.getByText("Workflow", { exact: true })).toBeVisible()
    const count = jobs.filter(
      (item) => item.workflowId === job.workflowId
    ).length
    await expect(
      header.getByText(`${count} ${count === 1 ? "job" : "jobs"} shown`, {
        exact: true,
      })
    ).toBeVisible()
    expect(
      await header.evaluate((element) =>
        Number.parseFloat(getComputedStyle(element).borderTopWidth)
      )
    ).toBeGreaterThanOrEqual(4)
    expect(await row.locator("xpath=ancestor::section").count()).toBe(2)
  }
  await expect(page.getByRole("table")).toHaveCount(1)
  await expect(page.getByRole("columnheader")).toHaveCount(8)
  const workflowGroups = page.locator("tbody[data-workflow-id]")
  await expect(workflowGroups).toHaveCount(
    new Set(jobs.map((job) => job.workflowId)).size
  )
  await expect(page.getByRole("alert")).toHaveCount(0)
})

test("shared navbar preserves Service history routes and submenu keyboard focus", async ({
  page,
}) => {
  await page.goto(filteredUrl)
  const trigger = page.getByRole("button", {
    name: "Service history menu",
    exact: true,
  })
  await trigger.focus()
  await page.keyboard.press("Enter")
  const menu = page.getByRole("menu", {
    name: "Service history teams",
    exact: true,
  })
  await expect(
    menu.getByRole("menuitem", { name: "All teams", exact: true })
  ).toHaveAttribute("href", "/service-history")
  const teamItem = menu.locator(
    `[role="menuitem"][aria-haspopup="menu"][data-team="${team}"]`
  )
  await expect(teamItem).toBeVisible()
  await teamItem.focus()
  await page.keyboard.press("ArrowRight")
  const submenu = page.getByRole("menu", {
    name: `${team} workflow types`,
    exact: true,
  })
  await expect(
    submenu.getByRole("menuitem", {
      name: "All workflow types",
      exact: true,
    })
  ).toHaveAttribute("href", `/t/${team}/services`)
  const typeLink = submenu.locator(
    `[role="menuitem"][href*="type=${encodeURIComponent(type)}"]`
  )
  await expect(typeLink).toBeVisible()
  const destination = new URL(
    (await typeLink.getAttribute("href"))!,
    page.url()
  )
  expect(destination.pathname).toBe(`/t/${team}/services`)
  expect(destination.searchParams.get("type")).toBe(type)
  await page.keyboard.press("Escape")
  await expect(trigger).toBeFocused()
  await expect(menu).not.toBeVisible()
})

test("Trigger now from a paused grouped table row persists a Manual run without changing saved schedule settings", async ({
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
    const row = page.locator(`tr[data-schedule-id="${schedule.id}"]`)
    await expect(row.getByText("Paused", { exact: true })).toBeVisible()
    const accepted = page.waitForResponse(
      (response) =>
        new URL(response.url()).pathname ===
          `/api/v1/schedules/${schedule.id}/run-now` &&
        response.request().method() === "POST"
    )
    await row.getByRole("button", { name: "Trigger now", exact: true }).focus()
    await page.keyboard.press("Enter")
    const response = await accepted
    expect(response.status()).toBe(202)
    expect(response.request().postData()).toBeNull()
    expect(
      z.uuid().safeParse(response.request().headers()["idempotency-key"])
        .success
    ).toBe(true)
    const { runId } = z.object({ runId: z.uuid() }).parse(await response.json())
    await expect(row.getByRole("status")).toContainText(
      "Run accepted in this series"
    )
    await expect(row.getByRole("status")).toContainText(
      "next fire and saved variables are unchanged"
    )
    await expect(
      row.getByRole("link", { name: runId, exact: true })
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
    await expect(row.getByText("Paused; no automatic fires")).toBeVisible()
    await expect(
      row.getByRole("button", { name: /resume|pause|delete|save/i })
    ).toHaveCount(0)
  } finally {
    const deleted = await request.delete(`/api/v1/schedules/${schedule.id}`, {
      headers: { "If-Match": etag! },
    })
    expect(deleted.status()).toBe(204)
  }
})

test("grouped recurring job tables and navbar remain accessible without page overflow or overlap across themes and widths", async ({
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
      const trigger = page.getByRole("button", {
        name: "Recurring jobs menu",
        exact: true,
      })
      await trigger.focus()
      await page.keyboard.press("Enter")
      const menu = page.getByRole("menu", {
        name: "Recurring jobs teams",
        exact: true,
      })
      const teamItem = menu.locator(
        `[role="menuitem"][aria-haspopup="menu"][data-team="${team}"]`
      )
      await expect(teamItem).toBeVisible()
      await teamItem.focus()
      await page.keyboard.press("ArrowRight")
      const submenu = page.getByRole("menu", {
        name: `${team} workflow types`,
        exact: true,
      })
      await expect(submenu).toBeVisible()
      for (const popup of [menu, submenu]) {
        const bounds = await popup.boundingBox()
        expect(bounds).not.toBeNull()
        expect(bounds!.x).toBeGreaterThanOrEqual(0)
        expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(width)
      }
      if (width === 390) {
        const menuAudit = await new AxeBuilder({ page })
          .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
          .analyze()
        expect(menuAudit.violations).toEqual([])
      }
      await page.keyboard.press("Escape")
      await expect(trigger).toBeFocused()
      const scrollRegion = page.getByRole("region", {
        name: /recurring jobs table$/,
      })
      await expect(scrollRegion).toHaveAttribute("tabindex", "0")
      await scrollRegion.focus()
      await expect(scrollRegion).toBeFocused()
      if (width === 390) {
        await page.keyboard.press("ArrowRight")
        await expect
          .poll(() => scrollRegion.evaluate((region) => region.scrollLeft))
          .toBeGreaterThan(0)
      }
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
