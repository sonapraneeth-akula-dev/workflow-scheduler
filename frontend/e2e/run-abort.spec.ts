import { randomUUID } from "node:crypto"
import AxeBuilder from "@axe-core/playwright"
import { expect, test } from "@playwright/test"
import { z } from "zod"
import { scheduleSchema } from "../src/lib/api/client"
import { parseJson } from "../src/lib/json"

test("confirmed Operator abort requires a reason and shows actual immutable attribution in live banner and history", async ({
  page,
  request,
}) => {
  const created = await request.post("/api/v1/schedules", {
    data: {
      workflowId: "ui-tests/release/workflow",
      name: `Abort browser ${randomUUID()}`,
      timezone: "Etc/UTC",
      interval: "01:00:00",
    },
  })
  expect(created.status()).toBe(201)
  const schedule = scheduleSchema.parse(parseJson(await created.text()))
  const etag = created.headers().etag
  if (!etag) throw new Error("Abort fixture schedule has no ETag.")
  try {
    const accepted = await request.post(
      `/api/v1/schedules/${schedule.id}/run-now`
    )
    expect(accepted.status()).toBe(202)
    const { runId } = z.object({ runId: z.uuid() }).parse(await accepted.json())
    await expect
      .poll(async () => {
        const state = await request.get(`/api/v1/runs/${runId}`)
        return state.status()
      })
      .toBe(200)
    let posts = 0
    let snapshots = 0
    page.on("request", (request) => {
      const path = new URL(request.url()).pathname
      if (request.method() === "POST" && path === `/api/v1/runs/${runId}/abort`)
        posts++
      if (request.method() === "GET" && path === `/api/v1/runs/${runId}`)
        snapshots++
    })
    await page.goto(`/runs/${runId}`)
    await page.getByRole("button", { name: "Cancel run", exact: true }).focus()
    await page.keyboard.press("Enter")
    const dialog = page.getByRole("alertdialog", { name: "Cancel this run?" })
    const confirm = dialog.getByRole("button", {
      name: "Confirm cancellation",
      exact: true,
    })
    await expect(confirm).toBeDisabled()
    await dialog.getByLabel("Cancellation reason").fill(" \t\n ")
    await expect(confirm).toBeDisabled()
    await dialog
      .getByRole("form", { name: "Confirm run cancellation" })
      .evaluate((form: HTMLFormElement) => form.requestSubmit())
    await expect(dialog.getByRole("alert")).toContainText("1-500")
    expect(posts).toBe(0)
    const reason = `Browser-confirmed ${randomUUID()}`
    await dialog.getByLabel("Cancellation reason").fill(reason)
    const axeDialog = await new AxeBuilder({ page })
      .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
      .analyze()
    expect(
      axeDialog.violations.filter(
        (issue) => issue.impact === "critical" || issue.impact === "serious"
      )
    ).toEqual([])
    const response = page.waitForResponse(
      (response) =>
        new URL(response.url()).pathname === `/api/v1/runs/${runId}/abort` &&
        response.request().method() === "POST"
    )
    await confirm.focus()
    await page.keyboard.press("Enter")
    const receiptResponse = await response
    expect(receiptResponse.status()).toBe(202)
    const receipt = z
      .object({
        by: z.string(),
        byName: z.string(),
        at: z.string(),
        reason: z.string(),
        source: z.literal("UI"),
      })
      .parse(await receiptResponse.json())
    expect(receipt.reason).toBe(reason)
    expect(posts).toBe(1)
    await expect(dialog).toBeHidden()
    const banner = page
      .getByRole("status")
      .filter({ hasText: "Recorded cancellation" })
    await expect(banner).toContainText("The run is cancelled (Aborted).")
    await expect(banner).toContainText(receipt.by)
    await expect(banner).toContainText(receipt.byName)
    await expect(banner.locator("time")).toHaveAttribute("datetime", receipt.at)
    await expect(banner.locator("time")).toHaveAttribute("title", receipt.at)
    await expect(banner).toContainText(reason)
    await expect(banner).toContainText("UI")
    await expect(
      page.getByRole("button", { name: "Cancel run", exact: true })
    ).toHaveCount(0)
    await expect(
      page.getByRole("region", { name: "Run controls", exact: true })
    ).toBeFocused()
    expect(snapshots).toBe(1)
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
    await page.reload()
    await expect(banner).toContainText(reason)
    await expect(banner.locator("time")).toHaveAttribute("datetime", receipt.at)
    await page
      .getByRole("link", { name: "Series history", exact: true })
      .click()
    const row = page
      .getByRole("table")
      .getByRole("row")
      .filter({ hasText: runId })
    await expect(row).toContainText("Aborted")
    await expect(row).toContainText(reason)
    await expect(row).toContainText(receipt.by)
    await expect(row.locator(`time[datetime="${receipt.at}"]`)).toHaveAttribute(
      "title",
      receipt.at
    )
    const after = await request.get(`/api/v1/schedules/${schedule.id}`)
    expect(after.headers().etag).toBe(etag)
    expect(scheduleSchema.parse(parseJson(await after.text())).paused).toBe(
      false
    )
  } finally {
    const deleted = await request.delete(`/api/v1/schedules/${schedule.id}`, {
      headers: { "If-Match": etag },
    })
    expect(deleted.status()).toBe(204)
  }
})
