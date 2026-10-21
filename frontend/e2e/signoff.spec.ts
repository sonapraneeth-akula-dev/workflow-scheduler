import AxeBuilder from "@axe-core/playwright"
import { expect, test, type Page } from "@playwright/test"
import { z } from "zod"

const fixture = z
  .object({
    workflowId: z.string().min(1),
    runId: z.uuid(),
    reportId: z.uuid(),
    operatorToken: z.string().min(1),
    viewerToken: z.string().min(1),
  })
  .parse(JSON.parse(process.env.WF_SIGNOFF_FIXTURE ?? "{}"))

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
  await page.goto(`/runs/${fixture.runId}`)
  await expect(
    page.getByRole("region", { name: "Workflow sign-off", exact: true })
  ).toBeVisible()
}

async function accessible(page: Page) {
  await page.evaluate(() =>
    Promise.allSettled(
      document.getAnimations().map((animation) => animation.finished)
    )
  )
  const report = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
    .analyze()
  expect(
    report.violations.filter(
      (issue) => issue.impact === "critical" || issue.impact === "serious"
    )
  ).toEqual([])
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth
    )
  ).toBe(true)
}

test("Operator approves independent run/report targets; Viewer sees immutable revocations and summaries", async ({
  page,
  browser,
}, info) => {
  test.setTimeout(120_000)
  const reason = `Reviewed <script> ${info.project.name}`
  const revoked = `Regression found ${info.project.name}`
  const reportReason = `Report evidence checked ${info.project.name}`
  const reportRevoked = `Report evidence withdrawn ${info.project.name}`
  await login(page, fixture.operatorToken)
  const workflow = page.getByRole("region", {
    name: "Workflow sign-off",
    exact: true,
  })
  await workflow
    .getByRole("button", { name: "Sign off workflow", exact: true })
    .click()
  const dialog = page.getByRole("alertdialog")
  await expect(
    dialog.getByRole("button", { name: "Confirm sign-off" })
  ).toBeDisabled()
  await expect(dialog).toContainText("not its reports or future runs")
  await dialog.getByLabel("Sign-off reason", { exact: true }).fill(reason)
  await accessible(page)
  await dialog.getByRole("button", { name: "Confirm sign-off" }).click()
  await expect(dialog).toHaveCount(0)
  await expect(workflow.getByText("Signed off", { exact: true })).toBeVisible()
  await expect(
    workflow.getByText("Browser reviewer", { exact: true }).first()
  ).toBeVisible()
  await page.getByRole("tab", { name: "Reports", exact: true }).click()
  const report = page.getByRole("region", {
    name: "Report sign-off",
    exact: true,
  })
  await report
    .getByRole("button", { name: "Sign off report", exact: true })
    .click()
  await dialog.getByLabel("Sign-off reason", { exact: true }).fill(reportReason)
  await dialog.getByRole("button", { name: "Confirm sign-off" }).click()
  await expect(dialog).toHaveCount(0)
  await expect(report.getByText("Signed off", { exact: true })).toBeVisible()
  await workflow
    .getByRole("button", { name: "Revoke sign-off", exact: true })
    .click()
  await dialog.getByLabel("Revocation reason", { exact: true }).fill(revoked)
  await dialog.getByRole("button", { name: "Confirm revocation" }).click()
  await expect(dialog).toHaveCount(0)
  await expect(workflow.getByText("Revoked", { exact: true })).toBeVisible()
  await expect(report.getByText("Signed off", { exact: true })).toBeVisible()
  await report
    .getByRole("button", { name: "Revoke sign-off", exact: true })
    .click()
  await dialog
    .getByLabel("Revocation reason", { exact: true })
    .fill(reportRevoked)
  await dialog.getByRole("button", { name: "Confirm revocation" }).click()
  await expect(dialog).toHaveCount(0)
  await expect(report.getByText("Revoked", { exact: true })).toBeVisible()
  await expect(
    page.getByRole("tab", { name: /Review report.*Revoked/ })
  ).toBeVisible()

  const context = await browser.newContext({
    baseURL: new URL(page.url()).origin,
    viewport: { width: 375, height: 900 },
  })
  try {
    const viewer = await context.newPage()
    await login(viewer, fixture.viewerToken)
    await viewer.evaluate(() => document.documentElement.classList.add("dark"))
    const history = viewer.getByRole("region", {
      name: "Workflow sign-off",
      exact: true,
    })
    await expect(history.getByText("Revoked", { exact: true })).toBeVisible()
    await expect(
      history.getByRole("button", { name: /Sign off workflow|Revoke sign-off/ })
    ).toHaveCount(0)
    await history.locator("summary").focus()
    await viewer.keyboard.press("Enter")
    await expect(history.getByText(reason, { exact: true })).toBeVisible()
    await expect(
      history.locator("ol").getByText(revoked, { exact: true })
    ).toBeVisible()
    await accessible(viewer)
    await viewer.getByRole("tab", { name: "Reports", exact: true }).click()
    const reportHistory = viewer.getByRole("region", {
      name: "Report sign-off",
      exact: true,
    })
    await expect(
      reportHistory.getByText("Revoked", { exact: true })
    ).toBeVisible()
    await reportHistory.locator("summary").click()
    await expect(
      reportHistory.getByText(reportReason, { exact: true })
    ).toBeVisible()
    await expect(
      reportHistory.getByRole("button", {
        name: /Sign off report|Revoke sign-off/,
      })
    ).toHaveCount(0)
    await viewer.goto(
      `/runs?workflowId=${encodeURIComponent(fixture.workflowId)}`
    )
    const results = viewer.getByRole("region", {
      name: "Run results",
      exact: true,
    })
    await expect(results).toContainText("Revoked")
    await expect(results).toContainText(`Browser reviewer: ${revoked}`)
    const [team, type, name] = fixture.workflowId
      .split("/")
      .map(encodeURIComponent)
    await viewer.goto(`/t/${team}/${type}/w/${name}/history`)
    await expect(
      viewer.getByRole("columnheader", { name: "Sign-off", exact: true })
    ).toBeVisible()
    await expect(viewer.getByRole("table")).toContainText(
      `Browser reviewer: ${revoked}`
    )
    await accessible(viewer)
  } finally {
    await context.close()
  }
})
