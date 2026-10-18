import { expect, type Page } from "@playwright/test"

export async function openTeamRunList(page: Page) {
  await page.goto("/runs?status=Succeeded")
  const team = page.locator('main a[href^="/t/"][href*="/runs"]').first()
  await expect(team).toBeVisible()
  await team.click()
  await expect(page.getByRole("form", { name: "Run filters" })).toBeVisible()
}
