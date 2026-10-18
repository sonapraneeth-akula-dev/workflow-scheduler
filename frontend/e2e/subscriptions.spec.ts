import AxeBuilder from "@axe-core/playwright"
import {
  expect,
  test,
  type APIRequestContext,
  type Page,
} from "@playwright/test"
import { z } from "zod"
import { subscriptionSchema } from "../src/features/subscriptions/api"

const fixture = z
  .object({
    workflow: z.string(),
    type: z.string(),
    scheduleId: z.uuid(),
    viewerToken: z.string().min(1),
    otherToken: z.string().min(1),
    foreignToken: z.string().min(1),
    pagingToken: z.string().min(1),
  })
  .parse(JSON.parse(process.env.WF_SUBSCRIPTION_FIXTURE ?? "{}"))
const [team, type, workflow] = fixture.workflow.split("/")
const workflowPath = `/t/${team}/${type}/w/${workflow}/history`
const headers = (token = fixture.viewerToken) => ({
  Authorization: `Bearer ${token}`,
})
async function login(
  page: Page,
  token = fixture.viewerToken,
  path = workflowPath
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
  await page.goto(path)
}
async function open(page: Page) {
  await page.getByRole("button", { name: "Subscribe", exact: true }).click()
  await expect(
    page.getByRole("form", { name: "Create notification subscription" })
  ).toBeVisible()
}
async function save(page: Page, status = 201) {
  const response = page.waitForResponse(
    (response) =>
      new URL(response.url()).pathname === "/api/v1/subscriptions" &&
      response.request().method() === "POST"
  )
  await page
    .getByRole("button", { name: "Save subscription", exact: true })
    .click()
  const result = await response
  expect(result.status()).toBe(status)
  return result
}
async function rows(
  request: APIRequestContext,
  scope: string,
  scopeId: string,
  token = fixture.viewerToken
) {
  const response = await request.get("/api/v1/subscriptions", {
    params: { scope, scopeId, limit: 100 },
    headers: headers(token),
  })
  expect(response.status()).toBe(200)
  return z
    .object({
      items: z.array(subscriptionSchema),
      nextCursor: z.string().nullable(),
    })
    .parse(await response.json()).items
}
async function accessible(page: Page) {
  const report = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
    .analyze()
  expect(
    report.violations.filter(
      (issue) => issue.impact === "critical" || issue.impact === "serious"
    )
  ).toEqual([])
  const layout = await page.evaluate(() => ({
    width: document.documentElement.scrollWidth,
    viewport: innerWidth,
    overflow: Array.from(document.querySelectorAll("main *"))
      .flatMap((element) => {
        const rect = element.getBoundingClientRect()
        return rect.right > innerWidth && rect.width > 0
          ? [
              {
                tag: element.tagName,
                text: element.textContent?.slice(0, 100),
                right: rect.right,
              },
            ]
          : []
      })
      .slice(0, 10),
  }))
  expect(layout.width, JSON.stringify(layout)).toBeLessThanOrEqual(
    layout.viewport
  )
}

test("workflow, type and schedule entry points persist scoped email, Teams and webhook subscriptions with accessible unsubscribe", async ({
  page,
  request,
  browserName,
}) => {
  test.setTimeout(60_000)
  await login(page)
  const suffix = `${browserName}-${crypto.randomUUID()}`
  const entries = [
    {
      scope: "workflow",
      scopeId: fixture.workflow,
      path: workflowPath,
      channel: "Email",
      label: "Email address",
      target: `${suffix}@example.test`,
      events: "Failed",
    },
    {
      scope: "type",
      scopeId: fixture.type,
      path: `/t/${team}/${type}`,
      channel: "Teams",
      label: "Teams destination reference",
      target: `secret://notifications/teams-${suffix}`,
      events: "Completed",
    },
    {
      scope: "schedule",
      scopeId: fixture.scheduleId,
      path: `/schedules/${fixture.scheduleId}`,
      channel: "Webhook",
      label: "Webhook URL",
      target: `https://notify.example.test/hook/${suffix}`,
      events: "Completed",
    },
  ]
  for (const entry of entries) {
    await page.goto(entry.path)
    await open(page)
    await page
      .getByRole("combobox", { name: "Channel", exact: true })
      .selectOption(entry.channel)
    await page
      .getByRole("combobox", { name: "Events", exact: true })
      .selectOption(entry.events)
    await page.getByLabel(entry.label, { exact: true }).fill(entry.target)
    if (entry.channel === "Webhook")
      await page
        .getByLabel("Signing secret reference", { exact: true })
        .fill("secret://notifications/signing")
    const receipt = subscriptionSchema.parse(await (await save(page)).json())
    expect(receipt).toMatchObject({
      scope: entry.scope,
      scopeId: entry.scopeId,
      channel: entry.channel,
      target: entry.target,
      events: [entry.events],
      secretRef:
        entry.channel === "Webhook" ? "secret://notifications/signing" : null,
    })
    const item = page.locator(`[data-subscription-id="${receipt.id}"]`)
    await expect(item).toContainText(entry.target)
    expect(
      (await rows(request, entry.scope, entry.scopeId)).find(
        (row) => row.id === receipt.id
      )
    ).toEqual(receipt)
    await accessible(page)
    await page.reload()
    await open(page)
    await expect(item).toContainText(entry.target)
    if (entry.scope === "workflow") {
      await page.goto(`/t/${team}/${type}/w/${workflow}/versions`)
      await expect(
        page.getByRole("heading", { name: "Versions", exact: true })
      ).toBeVisible()
      await open(page)
      await expect(item).toContainText(entry.target)
      await accessible(page)
    }
    await page.setViewportSize({ width: 375, height: 812 })
    await page.getByRole("button", { name: "Dark theme", exact: true }).click()
    await accessible(page)
    const removed = page.waitForResponse(
      (response) =>
        new URL(response.url()).pathname ===
          `/api/v1/subscriptions/${receipt.id}` &&
        response.request().method() === "DELETE"
    )
    await item
      .getByRole("button", {
        name: `Unsubscribe ${entry.channel}`,
        exact: true,
      })
      .click()
    expect((await removed).status()).toBe(204)
    await expect(item).toHaveCount(0)
    expect(
      (await rows(request, entry.scope, entry.scopeId)).some(
        (row) => row.id === receipt.id
      )
    ).toBe(false)
    await page.getByRole("button", { name: "Light theme", exact: true }).click()
    await page.setViewportSize({ width: 1280, height: 800 })
  }
})

test("another Viewer cannot read or delete personal rows; real 403 hides cached destinations without a retry loop and owner cleanup still works", async ({
  page,
  request,
  browser,
  browserName,
}) => {
  await login(page)
  await open(page)
  const target = `owned-${browserName}-${crypto.randomUUID()}@example.test`
  await page.getByLabel("Email address", { exact: true }).fill(target)
  const receipt = subscriptionSchema.parse(await (await save(page)).json())
  await expect(
    page.locator(`[data-subscription-id="${receipt.id}"]`)
  ).toContainText(target)
  await save(page, 409)
  await expect(page.getByRole("alert")).toContainText(/already|duplicate/i)
  await expect(page.getByLabel("Email address", { exact: true })).toHaveValue(
    target
  )
  let posts = 0
  page.on("request", (request) => {
    if (
      new URL(request.url()).pathname === "/api/v1/subscriptions" &&
      request.method() === "POST"
    )
      posts++
  })
  await page
    .getByRole("combobox", { name: "Channel", exact: true })
    .selectOption("Teams")
  await page
    .getByLabel("Teams destination reference", { exact: true })
    .fill("https://workflow.example.test/hook?sig=never-send")
  await page
    .getByRole("button", { name: "Save subscription", exact: true })
    .click()
  await expect(page.getByRole("alert")).toContainText(
    "configured Teams destination reference"
  )
  expect(posts).toBe(0)
  const other = await browser.newContext()
  try {
    const otherPage = await other.newPage()
    await login(otherPage, fixture.otherToken)
    await open(otherPage)
    await expect(otherPage.getByText(target, { exact: true })).toHaveCount(0)
    expect(
      await rows(request, "workflow", fixture.workflow, fixture.otherToken)
    ).toEqual([])
    const forbiddenDelete = await request.delete(
      `/api/v1/subscriptions/${receipt.id}`,
      { headers: headers(fixture.otherToken) }
    )
    expect(forbiddenDelete.status()).toBe(404)
    expect(
      (await rows(request, "workflow", fixture.workflow)).some(
        (row) => row.id === receipt.id
      )
    ).toBe(true)
    await accessible(otherPage)
  } finally {
    await other.close()
  }
  let denied = 0
  await page.route("**/api/v1/subscriptions?**", async (route) => {
    const response = await route.fetch({
      headers: {
        ...route.request().headers(),
        ...headers(fixture.foreignToken),
      },
    })
    expect(response.status()).toBe(403)
    denied++
    await route.fulfill({ response })
  })
  await page
    .getByRole("button", { name: "Refresh subscriptions", exact: true })
    .click()
  await expect(page.getByText(target, { exact: true })).toHaveCount(0)
  await expect(
    page.getByRole("form", { name: "Create notification subscription" })
  ).toHaveCount(0)
  await expect(
    page.getByText(`Email subscription ${receipt.id}`, { exact: true })
  ).toBeVisible()
  await expect.poll(() => denied).toBe(1)
  await page.waitForTimeout(500)
  expect(denied).toBe(1)
  await accessible(page)
  await page
    .locator(`[data-subscription-id="${receipt.id}"]`)
    .getByRole("button", { name: "Unsubscribe Email", exact: true })
    .click()
  await expect(
    page.getByText(`Email subscription ${receipt.id}`, { exact: true })
  ).toHaveCount(0)
  expect(
    (await rows(request, "workflow", fixture.workflow)).some(
      (row) => row.id === receipt.id
    )
  ).toBe(false)
  await page.setViewportSize({ width: 375, height: 812 })
  await page.getByRole("button", { name: "Dark theme", exact: true }).click()
  await accessible(page)
})

test("bounded keyset pagination loads every personal row once without crossing scope or identity", async ({
  page,
  request,
  browserName,
}) => {
  const prefix = `paging-${browserName}-${crypto.randomUUID()}`
  const ids: string[] = []
  try {
    for (let batch = 0; batch < 7; batch++) {
      const receipts = await Promise.all(
        Array.from(
          { length: Math.min(8, 51 - batch * 8) },
          async (_, index) => {
            const result = await request.post("/api/v1/subscriptions", {
              headers: headers(fixture.pagingToken),
              data: {
                scope: "workflow",
                scopeId: fixture.workflow,
                events: ["Completed"],
                channel: "Email",
                target: `${prefix}-${batch * 8 + index}@example.test`,
              },
            })
            expect(result.status()).toBe(201)
            return subscriptionSchema.parse(await result.json())
          }
        )
      )
      ids.push(...receipts.map((row) => row.id))
    }
    await login(page, fixture.pagingToken)
    await open(page)
    const items = page.locator("[data-subscription-id]")
    await expect(items).toHaveCount(50)
    await page
      .getByRole("button", { name: "Load more subscriptions", exact: true })
      .click()
    await expect(items).toHaveCount(51)
    expect(
      new Set(
        await items.evaluateAll((elements) =>
          elements.map((element) =>
            element.getAttribute("data-subscription-id")
          )
        )
      )
    ).toEqual(new Set(ids))
    await expect(
      page.getByRole("button", { name: "Load more subscriptions", exact: true })
    ).toHaveCount(0)
    await accessible(page)
  } finally {
    for (const id of ids) {
      const response = await request.delete(`/api/v1/subscriptions/${id}`, {
        headers: headers(fixture.pagingToken),
      })
      expect(response.status()).toBe(204)
    }
  }
})
