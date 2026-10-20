import AxeBuilder from "@axe-core/playwright"
import { expect, test, type APIRequestContext } from "@playwright/test"
import { z } from "zod"
import { jsonObjectSchema, jsonValueSchema, parseJson } from "../src/lib/json"

const workflowId = process.env.WF_CLONE_WORKFLOW ?? "ui-tests/release/workflow"
const editedKey = process.env.WF_CLONE_VARIABLE ?? "sequence"
const editedValue = jsonValueSchema.parse(
  parseJson(process.env.WF_CLONE_VALUE ?? "9223372036854775806")
)
const sourceSchema = z.object({
  runId: z.uuid(),
  workflowId: z.string(),
  versionId: z.uuid(),
  variables: jsonObjectSchema,
})
async function sourceRun(request: APIRequestContext) {
  const response = await request.get(
    `/api/v1/runs/history?${new URLSearchParams({
      workflowId,
      status: "Succeeded",
      outcome: "Run",
      limit: "1",
      ...(process.env.WF_CLONE_SERIES
        ? { seriesIds: process.env.WF_CLONE_SERIES }
        : {}),
    })}`
  )
  expect(response.status()).toBe(200)
  const { items } = z
    .object({ items: z.array(z.object({ runId: z.uuid() })) })
    .parse(await response.json())
  if (!items[0])
    throw new Error(`A successful source run is required for ${workflowId}.`)
  const inputs = await request.get(`/api/v1/runs/${items[0].runId}/clone`)
  expect(inputs.status()).toBe(200)
  return sourceSchema.parse(parseJson(await inputs.text()))
}

test("clone edits copied JSON, starts every node fresh and preserves the pinned source, lineage and schedule settings", async ({
  page,
  request,
}) => {
  test.setTimeout(120_000)
  const source = await sourceRun(request)
  const sourceBefore = await request.get(`/api/v1/runs/${source.runId}`)
  expect(sourceBefore.status()).toBe(200)
  const sourceSnapshot = z
    .object({ seriesId: z.uuid().nullable() })
    .parse(await sourceBefore.json())
  const schedulesPath = `/api/v1/schedules?${new URLSearchParams({ workflowId, limit: "100" })}`
  const schedulesBefore = await request.get(schedulesPath)
  expect(schedulesBefore.status()).toBe(200)
  const schedules = z
    .object({
      items: z.array(
        z.object({
          id: z.uuid(),
          rowVersion: z.int(),
          paused: z.boolean(),
          variables: jsonObjectSchema,
          versionPin: z.string().nullable(),
          nextFireAt: z.string().nullable(),
        })
      ),
    })
    .parse(parseJson(await schedulesBefore.text())).items
  await page.goto(`/runs/${source.runId}`)
  await page.getByRole("button", { name: "Clone run", exact: true }).click()
  const dialog = page.getByRole("dialog", { name: "Clone run", exact: true })
  const editor = dialog.getByRole("textbox", { name: "Variables (JSON)" })
  await expect(editor).toHaveValue(JSON.stringify(source.variables, null, 2))
  const edited = { ...source.variables, [editedKey]: editedValue }
  await editor.fill(JSON.stringify(edited))
  await dialog.getByRole("button", { name: "Format JSON" }).click()
  await expect(editor).toHaveValue(JSON.stringify(edited, null, 2))
  await expect(dialog).toContainText(source.versionId)
  await expect(dialog).toContainText("Every node executes again")
  const accepted = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname ===
        `/api/v1/runs/${source.runId}/clone` &&
      response.request().method() === "POST"
  )
  await dialog.getByRole("button", { name: "Confirm clone" }).focus()
  await page.keyboard.press("Enter")
  const response = await accepted
  expect(response.status()).toBe(202)
  expect(
    z.uuid().safeParse(response.request().headers()["idempotency-key"]).success
  ).toBe(true)
  expect(JSON.stringify(parseJson(response.request().postData()!))).toBe(
    JSON.stringify({ variables: edited })
  )
  const { runId } = z.object({ runId: z.uuid() }).parse(await response.json())
  expect(runId).not.toBe(source.runId)
  await expect(dialog.getByRole("status")).toContainText("Clone accepted")
  const link = dialog.getByRole("link", { name: `Open cloned run ${runId}` })
  await expect(link).toHaveAttribute("href", `/runs/${runId}`)
  await link.click()
  await expect(page).toHaveURL(`/runs/${runId}`)
  const lineage = page.getByRole("region", { name: "Run lineage", exact: true })
  await expect(lineage.getByRole("link", { name: source.runId })).toBeVisible()
  await expect(lineage).toContainText("No nodes reused from the source run.")
  const snapshot = await request.get(`/api/v1/runs/${runId}`)
  expect(snapshot.status()).toBe(200)
  const state = z
    .object({
      workflowId: z.string(),
      versionId: z.uuid(),
      parentRunId: z.uuid(),
      seriesId: z.uuid().nullable(),
      reusedNodes: z.record(z.string(), z.uuid()),
      nodes: z.record(z.string(), z.int()),
    })
    .parse(await snapshot.json())
  expect(state).toMatchObject({
    workflowId,
    versionId: source.versionId,
    parentRunId: source.runId,
    seriesId: sourceSnapshot.seriesId,
    reusedNodes: {},
  })
  expect(Object.values(state.nodes).every((status) => status !== 11)).toBe(true)
  if (process.env.WF_CLONE_EXECUTES) {
    await expect
      .poll(
        async () => {
          const stored = await request.get(`/api/v1/runs/${runId}/clone`)
          if (stored.status() === 409) return null
          expect(stored.status()).toBe(200)
          return JSON.stringify(
            sourceSchema.parse(parseJson(await stored.text())).variables
          )
        },
        { timeout: 90_000 }
      )
      .toBe(JSON.stringify(edited))
  }
  const sourceAfter = await request.get(`/api/v1/runs/${source.runId}/clone`)
  expect(sourceAfter.status()).toBe(200)
  expect(sourceSchema.parse(parseJson(await sourceAfter.text()))).toEqual(
    source
  )
  const schedulesAfter = await request.get(schedulesPath)
  expect(schedulesAfter.status()).toBe(200)
  const saved = z
    .object({
      items: z.array(
        z.object({
          id: z.uuid(),
          rowVersion: z.int(),
          paused: z.boolean(),
          variables: jsonObjectSchema,
          versionPin: z.string().nullable(),
          nextFireAt: z.string().nullable(),
        })
      ),
    })
    .parse(parseJson(await schedulesAfter.text())).items
  expect(saved).toEqual(schedules)
})

test("clone JSON editor is keyboard accessible in light and dark themes at mobile and desktop widths", async ({
  page,
  request,
}) => {
  test.setTimeout(90_000)
  const source = await sourceRun(request)
  const errors: string[] = []
  page.on("pageerror", (error) => errors.push(error.message))
  for (const colorScheme of ["light", "dark"] as const) {
    await page.emulateMedia({ colorScheme })
    for (const width of [390, 1440]) {
      await page.setViewportSize({ width, height: 1000 })
      await page.goto(`/runs/${source.runId}`)
      const trigger = page.getByRole("button", {
        name: "Clone run",
        exact: true,
      })
      await trigger.focus()
      await page.keyboard.press("Enter")
      const dialog = page.getByRole("dialog", {
        name: "Clone run",
        exact: true,
      })
      const editor = dialog.getByRole("textbox", { name: "Variables (JSON)" })
      await expect(editor).toHaveValue(
        JSON.stringify(source.variables, null, 2)
      )
      await editor.fill("[1,2]")
      await expect(dialog.getByRole("alert")).toContainText(
        "Variables must be a JSON object"
      )
      await expect(
        dialog.getByRole("button", { name: "Confirm clone" })
      ).toBeDisabled()
      await editor.fill(JSON.stringify(source.variables, null, 2))
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth)
      ).toBeLessThanOrEqual(width)
      const audit = await new AxeBuilder({ page })
        .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
        .analyze()
      expect(audit.violations).toEqual([])
      await page.keyboard.press("Escape")
      await expect(dialog).not.toBeVisible()
      await expect(trigger).toBeFocused()
    }
  }
  expect(errors).toEqual([])
})
