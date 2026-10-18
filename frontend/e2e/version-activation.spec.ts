import AxeBuilder from "@axe-core/playwright"
import { expect, test, type Page } from "@playwright/test"
import { z } from "zod"

const fixture = z
  .object({
    workflowId: z.string().min(1),
    original: z.uuid(),
    latest: z.uuid(),
    runId: z.uuid(),
    conflictWorkflowId: z.string().min(1),
    conflictTarget: z.uuid(),
    conflictLatest: z.uuid(),
    adminToken: z.string().min(1),
    viewerToken: z.string().min(1),
  })
  .parse(JSON.parse(process.env.WF_ACTIVATION_FIXTURE ?? "{}"))

function versionsPath(workflowId: string) {
  const [team, type, workflow] = workflowId.split("/")
  return `/t/${team}/${type}/w/${workflow}/versions`
}
async function login(
  page: Page,
  token: string,
  workflowId = fixture.workflowId
) {
  await page.goto("/")
  await expect(
    page.getByText("Sign in to manage workflow schedules.", { exact: true })
  ).toBeVisible()
  await page.evaluate(async (jwt) => {
    const path = "/scripts/browser-auth.ts"
    const { hydrateBrowserSession }: typeof import("../scripts/browser-auth") =
      await import(path)
    await hydrateBrowserSession(jwt)
  }, token)
  await page.goto(versionsPath(workflowId))
  await expect(page.getByRole("button", { name: "Sign out" })).toBeVisible()
  await expect(page.getByText("Development authentication")).toHaveCount(0)
  await expect(
    page.getByRole("link", { name: "1.0.0", exact: true })
  ).toBeVisible()
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
    await page.evaluate(() => document.documentElement.scrollWidth)
  ).toBeLessThanOrEqual(await page.evaluate(() => innerWidth))
}
function mutationRequests(page: Page) {
  const requests: string[] = []
  page.on("request", (request) => {
    if (new URL(request.url()).pathname.endsWith("/activate"))
      requests.push(`${request.method()} ${new URL(request.url()).pathname}`)
  })
  return requests
}
test("Admin confirms one exact immutable UUID, pending action is deduplicated, active refreshes and pins/latest remain", async ({
  page,
}) => {
  const requests = mutationRequests(page)
  await login(page, fixture.adminToken)
  const target = page.getByRole("button", {
    name: `Activate version 1.0.0 (${fixture.original})`,
    exact: true,
  })
  const current = page.getByRole("button", {
    name: `Activate version 1.0.1 (${fixture.latest})`,
    exact: true,
  })
  await expect(current).toBeDisabled()
  await accessible(page)
  await target.click()
  const dialog = page.getByRole("alertdialog")
  await expect(dialog).toContainText(fixture.workflowId)
  await expect(dialog).toContainText(`Version 1.0.0 (${fixture.original})`)
  await expect(dialog).toContainText(
    "Existing runs and pinned schedules keep their versions"
  )
  expect(requests).toEqual([])
  await accessible(page)
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click()
  await expect(dialog).toHaveCount(0)
  await expect(target).toBeFocused()
  expect(requests).toEqual([])
  await target.click()
  let release!: () => void
  let arrived!: () => void
  const hold = new Promise<void>((resolve) => {
    release = resolve
  })
  const received = new Promise<void>((resolve) => {
    arrived = resolve
  })
  const actionPath = `/api/v1/workflows/${fixture.workflowId}/versions/${fixture.original}/activate`
  await page.route(`**${actionPath}`, async (route) => {
    const response = await route.fetch()
    expect(response.status()).toBe(200)
    arrived()
    await hold
    await route.fulfill({ response })
  })
  try {
    await dialog
      .getByRole("button", { name: "Confirm activation", exact: true })
      .click()
    await received
    await expect(
      dialog.getByRole("button", { name: "Activating...", exact: true })
    ).toBeDisabled()
    await expect(
      dialog.getByRole("button", { name: "Cancel", exact: true })
    ).toBeDisabled()
    await page.keyboard.press("Escape")
    await expect(dialog).toBeVisible()
    expect(requests).toEqual([`POST ${actionPath}`])
  } finally {
    release()
  }
  await expect(dialog.getByRole("status")).toHaveText(
    "Version activated. Existing runs remain pinned."
  )
  await expect(
    dialog.getByRole("button", { name: "Confirm activation", exact: true })
  ).toBeDisabled()
  await accessible(page)
  await dialog.getByRole("button", { name: "Close", exact: true }).click()
  await expect(
    page.getByText(`Active version: ${fixture.original}.`, { exact: true })
  ).toBeVisible()
  await expect(
    page.getByText(`Latest version: ${fixture.latest}.`, { exact: true })
  ).toBeVisible()
  await expect(
    page.getByRole("button", { name: "Refresh versions", exact: true })
  ).toBeFocused()
  await expect(
    page.getByRole("link", { name: "1.0.1", exact: true })
  ).toHaveAttribute(
    "href",
    `${versionsPath(fixture.workflowId)}/${fixture.latest}`
  )
  await page.getByRole("button", { name: "Dark theme" }).click()
  await page.setViewportSize({ width: 375, height: 812 })
  await accessible(page)
  await current.click()
  await expect(dialog).toContainText(`Version 1.0.1 (${fixture.latest})`)
  await accessible(page)
  await dialog
    .getByRole("button", { name: "Confirm activation", exact: true })
    .click()
  await expect(dialog.getByRole("status")).toHaveText(
    "Version activated. Existing runs remain pinned."
  )
  await dialog.getByRole("button", { name: "Close", exact: true }).click()
  await expect(
    page.getByText(`Active version: ${fixture.latest}.`, { exact: true })
  ).toBeVisible()
  expect(requests).toEqual([
    `POST ${actionPath}`,
    `POST /api/v1/workflows/${fixture.workflowId}/versions/${fixture.latest}/activate`,
  ])
})
test("Viewer sees read-only history and cannot activate through the actual typed client", async ({
  page,
}) => {
  const requests = mutationRequests(page)
  await login(page, fixture.viewerToken)
  await expect(
    page.getByRole("button", { name: /^Activate version/ })
  ).toHaveCount(0)
  await accessible(page)
  await page.getByRole("button", { name: "Dark theme" }).click()
  await page.setViewportSize({ width: 375, height: 812 })
  await accessible(page)
  expect(requests).toEqual([])
  const status = await page.evaluate(
    async ({ workflowId, original }) => {
      const path = "/src/features/versions/api.ts"
      const { activateVersion }: typeof import("../src/features/versions/api") =
        await import(path)
      const clientPath = "/src/lib/api/client.ts"
      const { ApiError }: typeof import("../src/lib/api/client") = await import(
        clientPath
      )
      try {
        await activateVersion(workflowId, original)
        return 200
      } catch (error) {
        if (error instanceof ApiError) return error.status
        throw error
      }
    },
    { workflowId: fixture.workflowId, original: fixture.original }
  )
  expect(status).toBe(403)
  await page
    .getByRole("button", { name: "Refresh versions", exact: true })
    .click()
  await expect(
    page.getByText(`Active version: ${fixture.latest}.`, { exact: true })
  ).toBeVisible()
  await expect(
    page.getByRole("button", { name: /^Activate version/ })
  ).toHaveCount(0)
})
test("incompatible live schedule returns real 409, leaves active/config intact and retries only on confirmation", async ({
  page,
}) => {
  const requests = mutationRequests(page)
  await login(page, fixture.adminToken, fixture.conflictWorkflowId)
  await page
    .getByRole("button", {
      name: `Activate version 1.0.0 (${fixture.conflictTarget})`,
      exact: true,
    })
    .click()
  const dialog = page.getByRole("alertdialog")
  const confirm = dialog.getByRole("button", {
    name: "Confirm activation",
    exact: true,
  })
  for (let attempt = 1; attempt <= 2; attempt++) {
    await confirm.click()
    await expect(dialog.getByRole("alert")).toContainText(
      "Existing schedules are incompatible with this workflow version"
    )
    await expect(confirm).toBeEnabled()
    expect(requests).toHaveLength(attempt)
    await expect(dialog).not.toContainText("private-value")
    await expect(
      dialog.getByText("Version activated. Existing runs remain pinned.")
    ).toHaveCount(0)
  }
  await accessible(page)
  await page.setViewportSize({ width: 375, height: 812 })
  await accessible(page)
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click()
  await expect(
    page.getByText(`Active version: ${fixture.conflictLatest}.`, {
      exact: true,
    })
  ).toBeVisible()
  await expect(
    page.getByText(`Latest version: ${fixture.conflictLatest}.`, {
      exact: true,
    })
  ).toBeVisible()
  expect(requests).toHaveLength(2)
})
