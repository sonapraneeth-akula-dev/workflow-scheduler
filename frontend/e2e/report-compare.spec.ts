import AxeBuilder from "@axe-core/playwright"
import { expect, test } from "@playwright/test"
import { execFileSync } from "node:child_process"
import { randomUUID } from "node:crypto"
import { readFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { resolve } from "node:path"
import { z } from "zod"
import { scheduleSchema } from "../src/lib/api/client"
import { parseJson } from "../src/lib/json"
import { seedRendererReport } from "../scripts/seed-renderer-reports"

test("any two real series runs compare exact report identities, reuse each renderer and fail explicitly on missing or ambiguous matches", async ({
  page,
  request,
}) => {
  test.setTimeout(120_000)
  const fixture = z
    .object({
      directory: z.string().regex(/^wf-ui-[a-zA-Z0-9_-]+$/),
      container: z.string().regex(/^wf-ui-[a-f0-9-]+$/),
    })
    .parse(
      JSON.parse(
        await readFile(new URL("../.test-stack.json", import.meta.url), "utf8")
      )
    )
  const created = await request.post("/api/v1/schedules", {
    data: {
      workflowId: "report-tests/viewer/basic",
      name: `Report comparison ${randomUUID()}`,
      timezone: "Etc/UTC",
      interval: "01:00:00",
    },
  })
  expect(created.status()).toBe(201)
  const schedule = scheduleSchema.parse(parseJson(await created.text()))
  const sql = (statement: string) =>
    execFileSync(
      "docker",
      [
        "exec",
        fixture.container,
        "psql",
        "-U",
        "workflow",
        "-d",
        "workflow",
        "-At",
        "-v",
        "ON_ERROR_STOP=1",
        "-c",
        statement,
      ],
      { encoding: "utf8", timeout: 20_000 }
    ).trim()
  const storage = resolve(tmpdir(), fixture.directory)
  const runIds: string[] = []
  const reports: { id: string }[] = []
  const title = " Exact comparison \u03bb "
  const sourceKey = "$post/summary"
  try {
    for (const [index, bytes] of [
      Buffer.from("Left zero result"),
      Buffer.from("## Right one result\n\n**passed**"),
      Buffer.from("Right two result"),
    ].entries()) {
      const accepted = await request.post(
        `/api/v1/schedules/${schedule.id}/run-now`,
        { headers: { "Idempotency-Key": randomUUID() } }
      )
      expect(accepted.status()).toBe(202)
      const { runId } = z
        .object({ runId: z.uuid() })
        .parse(await accepted.json())
      runIds.push(runId)
      await expect
        .poll(() => sql(`SELECT count(*) FROM runs WHERE id='${runId}'`), {
          timeout: 20_000,
        })
        .toBe("1")
      reports.push(
        await seedRendererReport(storage, runId, sql, {
          kind: index === 1 ? "markdown" : "text",
          title,
          sourceKey,
          bytes,
        })
      )
    }
    const missing = await seedRendererReport(storage, runIds[0]!, sql, {
      kind: "text",
      title: "Only in left",
      sourceKey,
      bytes: Buffer.from("must not display as an empty comparison"),
    })
    await seedRendererReport(storage, runIds[1]!, sql, {
      kind: "text",
      title: missing.title,
      sourceKey: "$post/different-source",
      bytes: Buffer.from("wrong source, must not match"),
    })
    await page.goto(`/series/${schedule.seriesId}`)
    await page
      .getByRole("link", { name: "Compare reports", exact: true })
      .click()
    await expect(page).toHaveURL(`/series/${schedule.seriesId}/compare`)
    await expect(
      page.getByRole("heading", { name: "Series history" })
    ).toHaveCount(0)
    const leftPicker = page.getByRole("combobox", {
      name: "Left run",
      exact: true,
    })
    const rightPicker = page.getByRole("combobox", {
      name: "Right run",
      exact: true,
    })
    const reportPicker = page.getByRole("combobox", {
      name: "Report from left run",
    })
    await expect(leftPicker.getByRole("option")).toHaveCount(4)
    await leftPicker.selectOption(runIds[0]!)
    await rightPicker.selectOption(runIds[2]!)
    await reportPicker.selectOption(reports[0]!.id)
    const submit = page.getByRole("button", {
      name: "Compare selected reports",
    })
    await submit.click()
    const left = page.getByRole("region", { name: "Left report", exact: true })
    const right = page.getByRole("region", {
      name: "Right report",
      exact: true,
    })
    await expect(left.getByRole("region", { name: title })).toHaveText(
      "Left zero result"
    )
    await expect(right.getByRole("region", { name: title })).toHaveText(
      "Right two result"
    )
    await rightPicker.selectOption(runIds[1]!)
    await expect(left).toHaveCount(0)
    const matched = page.waitForResponse(
      (response) =>
        new URL(response.url()).pathname ===
        `/api/v1/series/${schedule.seriesId}/reports/compare`
    )
    await submit.click()
    const response = await matched
    expect(response.status()).toBe(200)
    const url = new URL(response.url())
    expect(url.searchParams.get("sourceKey")).toBe(sourceKey)
    expect(url.searchParams.get("title")).toBe(title)
    await expect(
      right.getByRole("heading", { name: "Right one result", exact: true })
    ).toBeVisible()
    await expect(left.getByRole("region", { name: title })).toHaveText(
      "Left zero result"
    )
    await expect(
      page.getByRole("button", { name: "Refresh comparison" })
    ).toBeEnabled()
    for (const dark of [false, true]) {
      await page.setViewportSize(
        dark ? { width: 375, height: 812 } : { width: 1440, height: 1080 }
      )
      await page.evaluate(async (dark) => {
        document.documentElement.classList.toggle("dark", dark)
        await new Promise<void>((done) => requestAnimationFrame(() => done()))
        await Promise.allSettled(
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
      const widths = await page.evaluate(() => ({
        viewport: innerWidth,
        document: document.documentElement.scrollWidth,
        elements: Array.from(document.querySelectorAll("body *"))
          .filter((element) => element.scrollWidth > element.clientWidth + 1)
          .map((element) => ({
            tag: element.tagName,
            className: element.className,
            clientWidth: element.clientWidth,
            scrollWidth: element.scrollWidth,
          }))
          .slice(0, 20),
      }))
      expect(widths.document, JSON.stringify(widths)).toBeLessThanOrEqual(
        widths.viewport
      )
    }
    await reportPicker.selectOption(missing.id)
    await expect(left).toHaveCount(0)
    const absent = page.waitForResponse((response) =>
      new URL(response.url()).pathname.endsWith("/reports/compare")
    )
    await submit.click()
    expect((await absent).status()).toBe(404)
    await expect(page.getByRole("alert")).toBeVisible()
    await expect(left).toHaveCount(0)
    await expect(right).toHaveCount(0)
    await reportPicker.selectOption(reports[0]!.id)
    await submit.click()
    await expect(
      right.getByRole("heading", { name: "Right one result" })
    ).toBeVisible()
    await expect(
      page.getByRole("button", { name: "Refresh comparison" })
    ).toBeEnabled()
    await seedRendererReport(storage, runIds[1]!, sql, {
      kind: "text",
      title,
      sourceKey,
      bytes: Buffer.from("duplicate must never be selected arbitrarily"),
    })
    const ambiguous = page.waitForResponse((response) =>
      new URL(response.url()).pathname.endsWith("/reports/compare")
    )
    await page.getByRole("button", { name: "Refresh comparison" }).click()
    expect((await ambiguous).status()).toBe(409)
    await expect(page.getByRole("alert")).toBeVisible()
    await expect(left).toHaveCount(0)
    await expect(right).toHaveCount(0)
    await rightPicker.selectOption(runIds[2]!)
    await submit.click()
    await expect(right.getByRole("region", { name: title })).toHaveText(
      "Right two result"
    )
    await page.reload()
    await expect(leftPicker).toHaveValue("")
    await expect(rightPicker).toHaveValue("")
    await expect(left).toHaveCount(0)
    await expect(submit).toBeDisabled()
  } finally {
    const current = await request.get(`/api/v1/schedules/${schedule.id}`)
    expect(current.status()).toBe(200)
    const etag = current.headers().etag
    expect(etag).toBeTruthy()
    expect(
      (
        await request.delete(`/api/v1/schedules/${schedule.id}`, {
          headers: { "If-Match": etag! },
        })
      ).status()
    ).toBe(204)
  }
})
