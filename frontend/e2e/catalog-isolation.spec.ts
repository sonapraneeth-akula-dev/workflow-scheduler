import AxeBuilder from "@axe-core/playwright"
import { expect, test, type Page } from "@playwright/test"
import { z } from "zod"

const settings = z
  .object({
    team: z.string().min(1),
    other: z.string().min(1),
    run: z.uuid(),
    otherRun: z.uuid(),
    teamToken: z.string().min(1),
    typeToken: z.string().min(1),
  })
  .parse({
    team: process.env.WF_CATALOG_TEAM,
    other: process.env.WF_CATALOG_OTHER_TEAM,
    run: process.env.WF_CATALOG_RUN,
    otherRun: process.env.WF_CATALOG_OTHER_RUN,
    teamToken: process.env.WF_CATALOG_TEAM_TOKEN,
    typeToken: process.env.WF_CATALOG_TYPE_TOKEN,
  })

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
  const layout = await page.evaluate(() => ({
    viewport: innerWidth,
    width: document.documentElement.scrollWidth,
    containers: Array.from(
      document.querySelectorAll("main > *, header > *")
    ).map((element) => ({
      tag: element.tagName,
      classes: element.className,
      width: element.getBoundingClientRect().width,
    })),
  }))
  expect(layout.width, JSON.stringify(layout)).toBeLessThanOrEqual(
    layout.viewport
  )
}

for (const scope of ["team", "type"] as const) {
  test(`${scope} Viewer cannot read another team's types, workflows or runs`, async ({
    page,
  }) => {
    const token = scope === "team" ? settings.teamToken : settings.typeToken
    const mutations: string[] = []
    page.on("request", (request) => {
      if (
        new URL(request.url()).pathname.startsWith("/api/") &&
        request.method() !== "GET"
      )
        mutations.push(request.method() + " " + new URL(request.url()).pathname)
    })
    await page.goto("/")
    await expect(
      page.getByText("Sign in to manage workflow schedules.", { exact: true })
    ).toBeVisible()
    await page.evaluate(async (jwt) => {
      const path = "/scripts/browser-auth.ts"
      const {
        hydrateBrowserSession,
      }: typeof import("../scripts/browser-auth") = await import(path)
      await hydrateBrowserSession(jwt)
    }, token)
    await page.reload()
    await expect(page.getByRole("button", { name: "Sign out" })).toBeVisible()
    await expect(page.getByText("Development authentication")).toHaveCount(0)
    await expect(
      page.getByRole("link", { name: settings.team, exact: true })
    ).toBeVisible()
    await expect(
      page.getByRole("link", { name: settings.other, exact: true })
    ).toHaveCount(0)
    await accessible(page)
    await page.getByRole("link", { name: settings.team, exact: true }).click()
    await expect(
      page.getByRole("link", { name: /^(sibling|release)$/ })
    ).toHaveText(scope === "team" ? ["sibling", "release"] : ["release"])
    if (scope === "type")
      await expect(page.getByText("Team-only sibling workflows")).toHaveCount(0)
    await accessible(page)
    await page.getByRole("link", { name: "release", exact: true }).click()
    const table = page.getByRole("table")
    await expect(table).toContainText("catalog-visible")
    await expect(table).toContainText(settings.run)
    await expect(table).toContainText("Queued")
    await expect(
      page.getByText("catalog-private", { exact: false })
    ).toHaveCount(0)
    await expect(
      page.getByText(settings.otherRun, { exact: false })
    ).toHaveCount(0)
    await accessible(page)
    const api = await page.evaluate(
      async ({ team, other, run, otherRun, expectedToken }) => {
        const catalogPath = "/src/features/catalog/api.ts"
        const {
          getTeams,
          getTypes,
          getWorkflows,
        }: typeof import("../src/features/catalog/api") = await import(
          catalogPath
        )
        const clientPath = "/src/lib/api/client.ts"
        const { publicApi }: typeof import("../src/lib/api/client") =
          await import(clientPath)
        const authPath = "/src/lib/auth/session.ts"
        const {
          getSession,
          accessToken,
        }: typeof import("../src/lib/auth/session") = await import(authPath)
        if ((await accessToken()) !== expectedToken)
          throw new Error(
            "The production client did not acquire the fixture JWT."
          )
        const session = await getSession()
        const teams = await getTeams()
        const types = await getTypes(team, undefined, {}, true)
        const workflows = await getWorkflows(
          team,
          "release",
          null,
          undefined,
          {},
          true
        )
        const foreignTypes = await publicApi.GET("/api/v1/teams/{team}/types", {
          params: { path: { team: other } },
        })
        const foreignWorkflows = await getWorkflows(
          other,
          "release",
          null,
          undefined,
          {},
          true
        )
        const ownRun = await publicApi.GET("/api/v1/runs/{id}", {
          params: { path: { id: run } },
        })
        const deniedRun = await publicApi.GET("/api/v1/runs/{id}", {
          params: { path: { id: otherRun } },
        })
        const deniedEvents = await publicApi.GET("/api/v1/runs/{id}/events", {
          params: { path: { id: otherRun }, query: { afterSeq: "0" } },
        })
        return {
          session,
          teams,
          types,
          workflows,
          foreignTypesStatus: foreignTypes.response.status,
          foreignWorkflows,
          ownRunStatus: ownRun.response.status,
          deniedRunStatus: deniedRun.response.status,
          deniedEventsStatus: deniedEvents.response.status,
        }
      },
      {
        team: settings.team,
        other: settings.other,
        run: settings.run,
        otherRun: settings.otherRun,
        expectedToken: token,
      }
    )
    expect(api.session?.isDevelopment).toBe(false)
    expect(api.teams.map((team) => team.team)).toEqual([settings.team])
    expect(api.teams[0]?.presentation?.typeOrder ?? null).toEqual(
      scope === "team" ? ["sibling", "release"] : null
    )
    expect(api.types.map((type) => type.name)).toEqual(
      scope === "team" ? ["release", "sibling"] : ["release"]
    )
    expect(
      api.workflows.items.map((workflow) => workflow.latestRun?.id)
    ).toEqual([settings.run])
    expect(api.foreignTypesStatus).toBe(404)
    expect(api.foreignWorkflows.items).toEqual([])
    expect(api.ownRunStatus).toBe(200)
    expect(api.deniedRunStatus).toBe(403)
    expect(api.deniedEventsStatus).toBe(403)
    expect(JSON.stringify(api)).not.toContain(settings.other)
    expect(JSON.stringify(api)).not.toContain(settings.otherRun)
    await page.getByRole("button", { name: "Dark theme" }).click()
    await page.setViewportSize({ width: 375, height: 812 })
    await accessible(page)
    for (const path of [
      `/t/${settings.other}`,
      `/t/${settings.other}/release`,
    ]) {
      await page.goto(path)
      await expect(
        page
          .getByRole("alert")
          .filter({ hasText: "This team is not available" })
      ).toBeVisible()
      await expect(page.getByRole("table")).toHaveCount(0)
      await expect(
        page.getByText("catalog-private", { exact: false })
      ).toHaveCount(0)
      await expect(
        page.getByText(settings.otherRun, { exact: false })
      ).toHaveCount(0)
      await accessible(page)
    }
    expect(mutations).toEqual([])
  })
}
