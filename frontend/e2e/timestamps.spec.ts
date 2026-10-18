import { expect, test, type Locator } from "@playwright/test"

async function expectLocalTimes(scope: Locator, locale: string) {
  await expect(scope.locator("time[datetime]").first()).toBeVisible()
  const times = await scope
    .locator("time[datetime]")
    .evaluateAll((elements, locale) => {
      const formatter = new Intl.DateTimeFormat(locale, {
        day: "2-digit",
        month: "short",
        year: "numeric",
        hour: "2-digit",
        minute: "2-digit",
        second: "2-digit",
        fractionalSecondDigits: 3,
        hourCycle: "h23",
        timeZoneName: "short",
      })
      return elements.map((element) => {
        const raw = element.getAttribute("datetime")!
        const parts = new Map(
          formatter
            .formatToParts(new Date(raw))
            .map(({ type, value }) => [type, value])
        )
        return {
          raw,
          title: element.getAttribute("title"),
          text: element.textContent,
          expected: `${parts.get("day")} ${parts.get("month")} ${parts.get("year")}, ${parts.get("hour")}:${parts.get("minute")}:${parts.get("second")}.${parts.get("fractionalSecond")} ${parts.get("timeZoneName")}`,
        }
      })
    }, locale)
  for (const time of times) {
    expect(time.text).toBe(time.expected)
    expect(time.title).toBe(time.raw)
    expect(time.text).toMatch(/\.\d{3} (?:IST|PST|PDT|UTC)$/)
  }
}

for (const [timezoneId, locale] of [
  ["Asia/Kolkata", "en-IN"],
  ["America/Los_Angeles", "en-US"],
  ["UTC", "en-GB"],
] as const) {
  test.describe(timezoneId, () => {
    test.use({ timezoneId })

    test("run lists, attempts, events and history show local milliseconds while retaining exact timestamps", async ({
      page,
    }) => {
      await page.goto("/runs?status=Succeeded")
      const main = page.locator("main")
      await expectLocalTimes(main, locale)
      await main.locator('a[href^="/runs/"]').first().click()
      await page.getByRole("tab", { name: "Timeline", exact: true }).click()
      await expectLocalTimes(page.getByRole("tabpanel"), locale)
      await page
        .getByRole("tab", { name: "System Events", exact: true })
        .click()
      await expectLocalTimes(page.getByRole("tabpanel"), locale)
      await page
        .getByRole("link", { name: "Workflow history", exact: true })
        .click()
      await expectLocalTimes(main, locale)
    })
  })
}
