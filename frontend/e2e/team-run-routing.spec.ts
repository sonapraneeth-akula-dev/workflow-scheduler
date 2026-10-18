import { expect, test } from "@playwright/test"

test("global Runs chooses a team without querying mixed runs; route scope survives search, clear and reload", async ({
  page,
}) => {
  const queries: URL[] = []
  page.on("request", (request) => {
    const url = new URL(request.url())
    if (url.pathname === "/api/v1/runs" && request.method() === "GET")
      queries.push(url)
  })
  await page.goto("/runs")
  const teamLink = page.locator('main a[href^="/t/"][href*="/runs"]').first()
  await expect(teamLink).toBeVisible()
  expect(queries).toHaveLength(0)
  const teamPath = (await teamLink.getAttribute("href"))!
  const team = decodeURIComponent(teamPath.split("/")[2]!)
  await teamLink.click()
  const form = page.getByRole("form", { name: "Run filters" })
  await expect(form).toBeVisible()
  await expect(
    page.getByRole("heading", { name: `${team} runs`, exact: true })
  ).toBeVisible()
  await expect.poll(() => queries.length).toBeGreaterThan(0)
  await form.getByLabel("Run status").selectOption("Succeeded")
  await form.getByRole("button", { name: "Search runs" }).click()
  await expect(page).toHaveURL(/status=Succeeded/)
  await page.reload()
  await expect(form.getByLabel("Run status")).toHaveValue("Succeeded")
  await form.getByRole("button", { name: "Clear filters" }).click()
  await expect(form.getByLabel("Run status")).toHaveValue("")
  expect(new URL(page.url()).pathname).toBe(
    new URL(teamPath, page.url()).pathname
  )
  expect(
    queries.every((query) => query.searchParams.get("team") === team)
  ).toBe(true)
  await page.goto(`/runs?team=${encodeURIComponent(team)}&status=Succeeded`)
  await expect(page).toHaveURL(
    new RegExp(`/t/${encodeURIComponent(team)}/runs\\?status=Succeeded$`)
  )
  await expect(form.getByLabel("Run status")).toHaveValue("Succeeded")
})
