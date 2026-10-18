import { readFile } from "node:fs/promises"
import AxeBuilder from "@axe-core/playwright"
import { expect, test } from "@playwright/test"
import { z } from "zod"
import { scheduleSchema } from "../src/lib/api/client"
import { parseJson } from "../src/lib/json"

test("code schedule shows applied provenance and commit link without mutation controls", async ({
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
  await page.goto(`/schedules/${fixture.codeScheduleId}`)
  await expect(page.getByRole("heading", { level: 1 })).toHaveText(
    "code-nightly"
  )
  await expect(page.getByText("Code schedule", { exact: true })).toBeVisible()
  await expect(
    page.getByText("workflows/ui-tests.schedules.json", { exact: true })
  ).toBeVisible()
  const commit = "a".repeat(40)
  await expect(
    page.getByRole("link", {
      name: `View schedule commit ${commit} (opens in a new tab)`,
    })
  ).toHaveAttribute(
    "href",
    `https://github.com/example/workflows/commit/${commit}`
  )
  await expect(
    page.getByRole("form", { name: "Schedule", exact: true })
  ).toHaveCount(0)
  for (const name of [
    "Save changes",
    "Delete schedule",
    "Pause schedule",
    "Resume schedule",
  ]) {
    await expect(page.getByRole("button", { name, exact: true })).toHaveCount(0)
  }
  const before = await request.get(
    `/api/v1/schedules/${fixture.codeScheduleId}`
  )
  expect(before.status()).toBe(200)
  const body = await before.text()
  const schedule = scheduleSchema.parse(parseJson(body))
  for (const method of ["PUT", "DELETE", "pause", "resume"]) {
    const response = await request.fetch(
      `/api/v1/schedules/${fixture.codeScheduleId}${["pause", "resume"].includes(method) ? `/${method}` : ""}`,
      {
        method: ["pause", "resume"].includes(method) ? "POST" : method,
        data: method === "PUT" ? {} : undefined,
      }
    )
    expect(response.status()).toBe(409)
  }
  const after = await request.get(`/api/v1/schedules/${fixture.codeScheduleId}`)
  expect(after.headers().etag).toBe(before.headers().etag)
  expect(scheduleSchema.parse(parseJson(await after.text()))).toEqual(schedule)
  for (const dark of [false, true]) {
    if (dark) {
      await page.getByRole("button", { name: "Dark theme" }).click()
      await page.setViewportSize({ width: 375, height: 812 })
      expect(
        await page.evaluate(
          () => document.documentElement.scrollWidth <= innerWidth
        )
      ).toBe(true)
    }
    const axe = await new AxeBuilder({ page })
      .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
      .analyze()
    expect(
      axe.violations.filter(
        (issue) => issue.impact === "critical" || issue.impact === "serious"
      )
    ).toEqual([])
  }
  await page.reload()
  await expect(page.getByText("Code schedule", { exact: true })).toBeVisible()
})
