import AxeBuilder from "@axe-core/playwright"
import { expect, test, type Page } from "@playwright/test"
import { z } from "zod"

const fixture = z
  .object({
    workflowId: z.string().min(1),
    source: z.uuid(),
    version: z.uuid(),
    operatorToken: z.string().min(1),
    viewerToken: z.string().min(1),
    placementMachine: z.string().min(1),
  })
  .parse(JSON.parse(process.env.WF_RERUN_FIXTURE ?? "{}"))

async function login(page: Page, jwt: string) {
  await page.goto("/")
  await expect(
    page.getByText("Sign in to manage workflow schedules.", { exact: true })
  ).toBeVisible()
  await page.evaluate(async (token) => {
    const path = "/scripts/browser-auth.ts"
    const { hydrateBrowserSession }: typeof import("../scripts/browser-auth") =
      await import(path)
    await hydrateBrowserSession(token)
  }, jwt)
  await page.goto(`/runs/${fixture.source}`)
  await expect(page.getByRole("button", { name: "Sign out" })).toBeVisible()
  await expect(
    page.getByText(fixture.workflowId, { exact: true })
  ).toBeVisible()
  await expect(
    page.getByRole("region", { name: "Run dependency graph" })
  ).toBeVisible()
  await expect(page.getByRole("alert")).toHaveCount(0)
}
async function accessible(page: Page) {
  await page.evaluate(() =>
    Promise.allSettled(
      document.getAnimations().map((animation) => animation.finished)
    )
  )
  const result = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
    .analyze()
  expect(
    result.violations.filter(
      (issue) => issue.impact === "critical" || issue.impact === "serious"
    )
  ).toEqual([])
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth
    )
  ).toBe(true)
}
test("Operator confirms immutable source; dropped real acceptance retries one key; queued rerun shows Reused badges and original links", async ({
  page,
}) => {
  const submissions: string[] = []
  const action = `/api/v1/runs/${fixture.source}/rerun`
  page.on("request", (request) => {
    if (new URL(request.url()).pathname === action)
      submissions.push(request.headers()["idempotency-key"] ?? "")
  })
  await login(page, fixture.operatorToken)
  const trigger = page.getByRole("button", {
    name: "Re-run from failed",
    exact: true,
  })
  await expect(trigger).toBeVisible()
  await accessible(page)
  await trigger.click()
  const dialog = page.getByRole("alertdialog")
  await expect(dialog).toContainText(fixture.source)
  await expect(dialog).toContainText(fixture.version)
  await expect(dialog).toContainText("variables and metadata")
  expect(submissions).toEqual([])
  await accessible(page)
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click()
  await expect(trigger).toBeFocused()
  expect(submissions).toEqual([])
  await trigger.click()
  await dialog
    .getByRole("checkbox", { name: "Change placement for rerun" })
    .check()
  await expect(
    dialog.getByRole("checkbox", { name: "Run on a single machine" })
  ).toBeChecked()
  await expect(
    dialog.getByRole("option", { name: fixture.placementMachine, exact: true })
  ).toHaveCount(1)
  await dialog
    .getByLabel("Machine", { exact: true })
    .selectOption(fixture.placementMachine)
  await accessible(page)
  let release!: () => void
  let arrived!: () => void
  const hold = new Promise<void>((resolve) => {
    release = resolve
  })
  const received = new Promise<void>((resolve) => {
    arrived = resolve
  })
  let accepted: string | undefined
  await page.route(
    `**${action}`,
    async (route) => {
      const response = await route.fetch()
      expect(route.request().postDataJSON().placement).toEqual({
        singleMachine: true,
        machine: fixture.placementMachine,
      })
      expect(response.status()).toBe(202)
      accepted = z
        .object({ runId: z.uuid() })
        .parse(await response.json()).runId
      arrived()
      await hold
      await route.abort("failed")
    },
    { times: 1 }
  )
  try {
    await dialog
      .getByRole("button", { name: "Confirm rerun", exact: true })
      .click()
    await received
    await expect(
      dialog.getByRole("button", { name: "Accepting rerun...", exact: true })
    ).toBeDisabled()
    await expect(
      dialog.getByRole("button", { name: "Cancel", exact: true })
    ).toBeDisabled()
    await dialog
      .getByRole("form", { name: "Confirm rerun" })
      .evaluate((form) => {
        form.dispatchEvent(
          new Event("submit", { bubbles: true, cancelable: true })
        )
        form.dispatchEvent(
          new Event("submit", { bubbles: true, cancelable: true })
        )
      })
    await page.keyboard.press("Escape")
    await expect(dialog).toBeVisible()
    expect(submissions).toHaveLength(1)
  } finally {
    release()
  }
  await expect(dialog.getByRole("alert")).toBeVisible()
  await expect(
    dialog.getByRole("button", { name: "Retry rerun", exact: true })
  ).toBeEnabled()
  expect(submissions).toHaveLength(1)
  // The server committed the command; only its HTTP response was lost.
  await page.keyboard.press("Escape")
  await expect(trigger).toBeFocused()
  await trigger.click()
  await dialog.getByRole("button", { name: "Retry rerun", exact: true }).click()
  await expect(dialog.getByRole("status")).toContainText(
    "Execution may still be queued"
  )
  expect(submissions).toHaveLength(2)
  expect(submissions[0]).toMatch(/^[0-9a-f-]{36}$/)
  expect(submissions[1]).toBe(submissions[0])
  await expect(dialog.getByRole("link")).toHaveAttribute(
    "href",
    `/runs/${accepted}`
  )
  await expect(
    dialog.getByRole("button", { name: "Confirm rerun", exact: true })
  ).toBeDisabled()
  await accessible(page)
  await dialog.getByRole("link").click()
  await expect(page).toHaveURL(new RegExp(`/runs/${accepted}$`))
  await expect(page.getByLabel("Run placement override")).toContainText(
    fixture.placementMachine
  )
  const lineage = page.getByRole("region", { name: "Run lineage" })
  await expect(lineage).toContainText("did not execute new attempts")
  await expect(
    lineage.getByRole("link", { name: fixture.source, exact: true })
  ).toHaveAttribute("href", `/runs/${fixture.source}`)
  await expect(lineage.getByText("Reused", { exact: true })).toHaveCount(2)
  await expect(
    lineage.getByRole("link", {
      name: `build: original outputs in run ${fixture.source}`,
    })
  ).toHaveAttribute("href", `/runs/${fixture.source}`)
  await expect(
    lineage.getByRole("link", {
      name: `deploy-eu: original outputs in run ${fixture.source}`,
    })
  ).toHaveAttribute("href", `/runs/${fixture.source}`)
  await expect(
    page.getByRole("region", { name: "Run dependency graph" })
  ).toHaveAttribute("data-version-id", fixture.version)
  await expect(page.getByRole("alert")).toHaveCount(0)
  await accessible(page)
  await page.getByRole("button", { name: "Dark theme" }).click()
  await page.setViewportSize({ width: 375, height: 812 })
  await accessible(page)
  await lineage.getByRole("link", { name: fixture.source, exact: true }).click()
  await expect(page).toHaveURL(new RegExp(`/runs/${fixture.source}$`))
  await trigger.click()
  await accessible(page)
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click()
})
test("manual trigger picker submits a named single-machine override through the real API", async ({
  page,
}) => {
  await login(page, fixture.operatorToken)
  const [team, kind, workflow] = fixture.workflowId.split("/")
  await page.goto(
    `/t/${team}/${kind}/w/${workflow}/versions/${fixture.version}`
  )
  await page.getByRole("button", { name: "Trigger run", exact: true }).click()
  const dialog = page.getByRole("dialog")
  await expect(dialog).toContainText(fixture.version)
  await dialog
    .getByRole("checkbox", { name: "Run on a single machine" })
    .check()
  await expect(
    dialog.getByRole("option", { name: fixture.placementMachine, exact: true })
  ).toHaveCount(1)
  await dialog
    .getByLabel("Machine", { exact: true })
    .selectOption(fixture.placementMachine)
  await accessible(page)
  const response = page.waitForResponse(
    (reply) =>
      new URL(reply.url()).pathname === "/api/v1/runs" &&
      reply.request().method() === "POST"
  )
  await dialog.getByRole("button", { name: "Accept run", exact: true }).click()
  const accepted = await response
  expect(accepted.status()).toBe(202)
  expect(accepted.request().postDataJSON().placement).toEqual({
    singleMachine: true,
    machine: fixture.placementMachine,
  })
  const receipt = z.object({ runId: z.uuid() }).parse(await accepted.json())
  await dialog.getByRole("link", { name: receipt.runId, exact: true }).click()
  await expect(page.getByLabel("Run placement override")).toContainText(
    fixture.placementMachine
  )
  await accessible(page)
})
test("Viewer has no rerun action and direct typed API returns actual 403", async ({
  page,
}) => {
  await login(page, fixture.viewerToken)
  await expect(
    page.getByRole("button", { name: "Re-run from failed", exact: true })
  ).toHaveCount(0)
  await accessible(page)
  const status = await page.evaluate(async (parent) => {
    const path = "/src/features/runs/rerun-api.ts"
    const { rerunRun }: typeof import("../src/features/runs/rerun-api") =
      await import(path)
    const clientPath = "/src/lib/api/client.ts"
    const { ApiError }: typeof import("../src/lib/api/client") = await import(
      clientPath
    )
    try {
      await rerunRun(parent, crypto.randomUUID())
      return 202
    } catch (error) {
      if (error instanceof ApiError) return error.status
      throw error
    }
  }, fixture.source)
  expect(status).toBe(403)
  await page.getByRole("button", { name: "Dark theme" }).click()
  await page.setViewportSize({ width: 375, height: 812 })
  await accessible(page)
})
