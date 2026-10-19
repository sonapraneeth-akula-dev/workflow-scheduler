import { expect, test, type Page } from "@playwright/test"

async function openWorkflowHistory(page: Page) {
  await page.goto("/service-history")
  await page.locator('main a[href^="/t/"][href*="/services"]').first().click()
  await page
    .getByRole("link", { name: /^History for / })
    .first()
    .click()
  await expect(
    page.getByRole("heading", { name: "Workflow history", exact: true })
  ).toBeVisible()
}

for (const width of [320, 768, 2000]) {
  test(`workflow header stays compact and usable at ${width}px`, async ({
    page,
  }) => {
    await page.setViewportSize({ width, height: 1000 })
    await openWorkflowHistory(page)
    const summary = page.getByRole("region", {
      name: "Workflow history summary",
    })
    await expect(
      summary.getByRole("link", { name: "Workflow versions" })
    ).toBeVisible()
    await expect(
      summary.getByRole("link", { name: "Metrics dashboard" })
    ).toBeVisible()
    await expect(
      summary.getByRole("button", { name: "Trigger run", exact: true })
    ).toBeVisible()
    await expect(
      summary.getByRole("button", { name: "Subscribe", exact: true })
    ).toBeVisible()
    expect((await summary.boundingBox())!.height).toBeLessThan(
      width < 640 ? 200 : 140
    )
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth)
    ).toBeLessThanOrEqual(width)
    if (width >= 1280) {
      expect(
        (await summary
          .getByRole("button", { name: "Trigger run", exact: true })
          .boundingBox())!.width
      ).toBeLessThan(200)
      expect((await summary.boundingBox())!.height).toBeLessThan(80)
    }
  })
}

test("workflow header links open registered releases and metric content", async ({
  page,
}) => {
  await openWorkflowHistory(page)
  await page
    .getByRole("link", { name: "Workflow versions", exact: true })
    .click()
  await expect(
    page.getByRole("heading", { name: "Versions", exact: true })
  ).toBeVisible()
  await expect(page.getByText("Loading version history...")).toHaveCount(0)
  await expect(
    page.getByText("Active version:", { exact: false })
  ).toBeVisible()
  await expect(page.getByRole("alert")).toHaveCount(0)
  await page.goBack()
  await page
    .getByRole("link", { name: "Metrics dashboard", exact: true })
    .click()
  await expect(
    page.getByRole("heading", { name: "workflow dashboard", exact: true })
  ).toBeVisible()
  await expect(
    page.getByText("Checking dashboard access and loading rankings...")
  ).toHaveCount(0)
  await expect(
    page
      .getByRole("region", { name: "Metric dashboard" })
      .getByRole("status")
      .first()
  ).toContainText("(UTC). Top")
  await expect(page.getByRole("alert")).toHaveCount(0)
})
