import AxeBuilder from "@axe-core/playwright"
import { expect, test, type Page } from "@playwright/test"
import { z } from "zod"

const fixture = z
  .object({
    pool: z.string().min(1),
    machineId: z.uuid(),
    machineName: z.string().min(1),
    reservedRunId: z.uuid(),
    adminToken: z.string().min(1),
    viewerToken: z.string().min(1),
  })
  .parse(JSON.parse(process.env.WF_MACHINE_FIXTURE ?? "{}"))

async function login(page: Page, token: string) {
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
  await page.goto(`/machines?pool=${fixture.pool}`)
  await expect(
    page.getByRole("heading", { name: "Machines", exact: true })
  ).toBeVisible()
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
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth
    )
  ).toBe(true)
}
const headers = { authorization: `Bearer ${fixture.adminToken}` }

test("machine filter dropdown arrows stay centered inside controls beside taller label filters", async ({
  page,
}) => {
  await login(page, fixture.adminToken)
  const form = page.getByRole("form", { name: "Machine filters" })
  for (const width of [1920, 1440, 1280, 768, 375]) {
    await page.setViewportSize({ width, height: 900 })
    for (const theme of ["light", "dark"]) {
      await page.evaluate((value) => {
        document.documentElement.classList.toggle("dark", value === "dark")
      }, theme)
      for (const label of ["OS", "Health"]) {
        const select = form.getByRole("combobox", { name: label, exact: true })
        const control = await select.boundingBox()
        const wrapper = await select.locator("..").boundingBox()
        const arrow = await select
          .locator("..")
          .locator('[data-slot="native-select-icon"]')
          .boundingBox()
        expect(control).not.toBeNull()
        expect(wrapper).not.toBeNull()
        expect(arrow).not.toBeNull()
        expect(Math.abs(wrapper!.height - control!.height)).toBeLessThan(1)
        expect(arrow!.y).toBeGreaterThanOrEqual(control!.y)
        expect(arrow!.y + arrow!.height).toBeLessThanOrEqual(
          control!.y + control!.height
        )
        expect(
          Math.abs(
            arrow!.y + arrow!.height / 2 - (control!.y + control!.height / 2)
          )
        ).toBeLessThan(1)
      }
      expect(
        await form.evaluate((element) => {
          const bounds = element.getBoundingClientRect()
          return (
            bounds.right <= innerWidth && element.scrollWidth <= bounds.width
          )
        })
      ).toBe(true)
    }
  }
})

test("Admin sees mixed fleet and held reservation, filters, edits audited metadata and receives cross-page heartbeat updates", async ({
  page,
  request,
}) => {
  test.setTimeout(90_000)
  expect(
    (
      await request.patch(`/api/v1/machines/${fixture.machineId}`, {
        headers,
        data: { pool: fixture.pool, labels: { region: "eu" } },
      })
    ).status()
  ).toBe(204)
  expect(
    (
      await request.post("/api/fixture/machine-heartbeat/false", { headers })
    ).status()
  ).toBe(204)
  expect(
    (
      await request.post("/api/fixture/machine-capacity/false", { headers })
    ).status()
  ).toBe(204)
  await login(page, fixture.adminToken)
  await expect(page.locator("tbody tr")).toHaveCount(2)
  await expect(page.getByText("windows / x64", { exact: true })).toBeVisible()
  await expect(page.getByText("linux / x64", { exact: true })).toBeVisible()
  await expect(
    page
      .getByRole("row")
      .filter({
        has: page.getByRole("link", { name: fixture.machineName, exact: true }),
      })
      .getByRole("cell", { name: "Healthy", exact: true })
  ).toBeVisible()
  await expect(
    page.getByText("Release pending physical acknowledgement", { exact: true })
  ).toBeVisible()
  await expect(
    page.getByRole("link", { name: `Run ${fixture.reservedRunId}` })
  ).toBeVisible()
  await accessible(page)
  await page.getByLabel("OS", { exact: true }).selectOption("windows")
  await page
    .getByLabel("Label filters (one key=value per line)")
    .fill("region=eu")
  await page
    .getByRole("button", { name: "Filter machines", exact: true })
    .click()
  await expect(page.locator("tbody tr")).toHaveCount(1)
  await page.reload()
  await expect(page.getByLabel("OS", { exact: true })).toHaveValue("windows")
  await expect(
    page.getByLabel("Label filters (one key=value per line)")
  ).toHaveValue("region=eu")
  await expect(page.locator("tbody tr")).toHaveCount(1)
  const observer = await page.context().newPage()
  let observerResyncs = 0
  observer.on("websocket", (socket) => {
    socket.on("framereceived", ({ payload }) => {
      if (payload.toString().includes("Resync")) observerResyncs++
    })
  })
  await login(observer, fixture.adminToken)
  await expect(observer.locator("tbody tr")).toHaveCount(2)
  await expect(
    observer.getByRole("status").filter({ hasText: "Fleet updates: live" })
  ).toBeVisible()
  await page
    .getByRole("link", { name: fixture.machineName, exact: true })
    .click()
  await expect(
    page.getByRole("heading", { name: fixture.machineName, exact: true })
  ).toBeVisible()
  await expect(page.getByText("0%", { exact: true })).toBeVisible()
  await expect(
    page.getByText("Unverified: no capacity has been set for any profile.")
  ).toBeVisible()
  await page
    .getByRole("button", { name: "Edit labels and pool", exact: true })
    .click()
  const dialog = page.getByRole("alertdialog")
  await expect(dialog).toBeVisible()
  await accessible(page)
  await page.getByLabel("Pool", { exact: true }).fill(fixture.pool + "-edited")
  await page
    .getByLabel("Labels (one key=value per line)")
    .fill("region=us <script>\nkind=gpu")
  await page.getByRole("button", { name: "Save metadata", exact: true }).click()
  await expect(dialog).toHaveCount(0)
  await expect(
    page.getByText(fixture.pool + "-edited", { exact: true })
  ).toBeVisible()
  await expect(
    page.getByText("kind=gpu\nregion=us <script>", { exact: true })
  ).toBeVisible()
  await expect(observer.locator("tbody tr")).toHaveCount(1)
  await expect(
    observer.getByRole("link", { name: fixture.machineName, exact: true })
  ).toHaveCount(0)
  await observer.goto(`/machines?pool=${fixture.pool}-edited`)
  await expect(observer.locator("tbody tr")).toHaveCount(1)
  await expect(
    observer.getByText("kind=gpu\nregion=us <script>", { exact: true })
  ).toBeVisible()
  expect(
    (
      await request.post("/api/fixture/machine-heartbeat/true", { headers })
    ).status()
  ).toBe(204)
  await expect(page.getByText("37%", { exact: true })).toBeVisible()
  await expect(observer.getByText(/1 \/ 4 task slots used/)).toBeVisible()
  await observer.goto(`/machines/${fixture.machineId}`)
  await expect(
    observer.getByRole("status").filter({ hasText: "Fleet updates: live" })
  ).toBeVisible()
  await expect(
    observer.getByText("Unverified: no capacity has been set for any profile.")
  ).toBeVisible()
  expect(
    (
      await request.post("/api/fixture/machine-capacity/true", { headers })
    ).status()
  ).toBe(204)
  const capacities = page.getByRole("region", {
    name: "Capacity table",
    exact: true,
  })
  const observedCapacities = observer.getByRole("region", {
    name: "Capacity table",
    exact: true,
  })
  const small = capacities
    .getByRole("row")
    .filter({ has: page.getByRole("cell", { name: "small", exact: true }) })
  const observedSmall = observedCapacities
    .getByRole("row")
    .filter({ has: observer.getByRole("cell", { name: "small", exact: true }) })
  await expect(
    small.getByRole("cell", { name: "Calibrated", exact: true })
  ).toBeVisible()
  await expect(
    observedSmall.getByRole("cell", { name: "1000", exact: true })
  ).toBeVisible()
  await small.getByRole("button").click()
  await expect(page.getByLabel("Transport", { exact: true })).toHaveValue(
    "http"
  )
  await expect(page.getByLabel("Capacity (requests/s)")).toHaveValue("1000")
  await page.getByLabel("Capacity (requests/s)").fill("1234")
  await accessible(page)
  const beforeCapacitySave = observerResyncs
  await page.getByRole("button", { name: "Save capacity", exact: true }).click()
  await expect(page.getByRole("alertdialog")).toHaveCount(0)
  await expect(
    small.getByRole("cell", { name: "Manual", exact: true })
  ).toBeVisible()
  await expect(
    small.getByRole("cell", { name: "1234", exact: true })
  ).toBeVisible()
  await expect.poll(() => observerResyncs).toBeGreaterThan(beforeCapacitySave)
  await expect(
    observedSmall.getByRole("cell", { name: "1234", exact: true })
  ).toBeVisible()
  await expect(
    observedSmall.getByText("Unverified (manual; not calibrated)", {
      exact: true,
    })
  ).toBeVisible()
  const large = capacities
    .getByRole("row")
    .filter({ has: page.getByRole("cell", { name: "large", exact: true }) })
  await expect(
    large.getByRole("cell", { name: "Calibrated", exact: true })
  ).toBeVisible()
  await expect(
    large.getByRole("cell", { name: "3000", exact: true })
  ).toBeVisible()
  await page.reload()
  await expect(
    page.getByText(fixture.pool + "-edited", { exact: true })
  ).toBeVisible()
  await expect(
    small.getByRole("cell", { name: "1234", exact: true })
  ).toBeVisible()
  await page.setViewportSize({ width: 375, height: 812 })
  await page.getByRole("button", { name: "Dark theme" }).click()
  await accessible(page)
  await observer.setViewportSize({ width: 375, height: 812 })
  await accessible(observer)
  await page
    .getByRole("button", { name: "Set manual capacity", exact: true })
    .click()
  await accessible(page)
  await page.getByRole("button", { name: "Cancel", exact: true }).click()
  await page
    .getByRole("button", { name: "Edit labels and pool", exact: true })
    .click()
  await page.getByLabel("Labels (one key=value per line)").fill("")
  await page.getByRole("button", { name: "Save metadata", exact: true }).click()
  await expect(page.getByText("No labels", { exact: true })).toBeVisible()
  await expect(observer.getByText("No labels", { exact: true })).toBeVisible()
  await page.route("**/api/v1/machines", async (route) => {
    const response = await route.fetch({
      headers: {
        ...route.request().headers(),
        authorization: `Bearer ${fixture.viewerToken}`,
      },
    })
    expect(response.status()).toBe(403)
    await route.fulfill({ response })
  })
  await page
    .getByRole("button", { name: "Refresh machines", exact: true })
    .click()
  await expect(page.getByRole("alert")).toContainText(
    /403|required role|Forbidden/
  )
  await expect(
    page.getByRole("button", { name: "Edit labels and pool" })
  ).toHaveCount(0)
  await expect(
    page.getByRole("heading", { name: fixture.machineName, exact: true })
  ).toHaveCount(0)
  await observer.close()
})

test("Viewer direct machine list and detail access exposes neither metadata nor edit controls", async ({
  page,
  request,
}) => {
  await login(page, fixture.viewerToken)
  await expect(page.getByRole("alert")).toContainText("required role")
  await expect(page.getByRole("table")).toHaveCount(0)
  await page.goto(`/machines/${fixture.machineId}`)
  await expect(page.getByRole("alert")).toContainText("required role")
  await expect(
    page.getByRole("button", { name: "Edit labels and pool" })
  ).toHaveCount(0)
  expect(
    (
      await request.get("/api/v1/machines", {
        headers: { authorization: `Bearer ${fixture.viewerToken}` },
      })
    ).status()
  ).toBe(403)
  await accessible(page)
})
