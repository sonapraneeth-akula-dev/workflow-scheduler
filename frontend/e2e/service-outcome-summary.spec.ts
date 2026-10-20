import AxeBuilder from "@axe-core/playwright"
import { expect, test } from "@playwright/test"

for (const theme of ["light", "dark"] as const) {
  for (const width of [390, 1440]) {
    test(`outcome summary matches unique visible runs and supports keyboard disclosure at ${width}px in ${theme}`, async ({
      page,
    }) => {
      await page.setViewportSize({ width, height: 1000 })
      await page.addInitScript(
        (preference) => localStorage.setItem("kardia.theme", preference),
        theme
      )
      const errors: string[] = []
      page.on("pageerror", (error) => errors.push(error.message))
      const team = process.env.WF_SUMMARY_TEAM ?? "ui-tests"
      await page.goto(`/t/${encodeURIComponent(team)}/services`)
      const summary = page.locator("summary").filter({
        hasText: /^Run outcome summary$/,
      })
      const accordion = summary.locator("..")
      await expect(summary).toBeVisible()
      await expect(
        page.getByRole("button", { name: "Refresh service history" })
      ).toBeEnabled()
      await expect(accordion).toHaveAttribute("open", "")
      await expect(page.getByRole("alert")).toHaveCount(0)
      await expect(page.locator("html")).toHaveCSS("color-scheme", theme)
      const runs = await page
        .locator("[data-run-id]")
        .evaluateAll((links) =>
          Array.from(
            new Map(
              links.map((link) => [
                link.getAttribute("data-run-id"),
                link.getAttribute("data-run-status"),
              ])
            ).values()
          )
        )
      const failed = runs.filter((status) => status === "Failed").length
      const timedOut = runs.filter((status) => status === "TimedOut").length
      const count = async (label: string) =>
        Number(
          await accordion
            .getByText(label, { exact: true })
            .locator("..")
            .locator("dd")
            .textContent()
        )
      expect(await count("Failed runs")).toBe(failed)
      expect(await count("Timed out runs")).toBe(timedOut)
      const classified =
        (await count("Infra failures")) + (await count("Service failures"))
      expect(classified + (await count("Unknown cause"))).toBe(
        failed + timedOut
      )
      const categoryCounts = await accordion
        .getByRole("group", { name: "Failure categories", exact: true })
        .locator("li > span:last-child")
        .allTextContents()
      expect(
        categoryCounts.reduce((sum, value) => sum + Number(value), 0)
      ).toBe(classified)
      await summary.focus()
      await page.keyboard.press("Enter")
      await expect(accordion).not.toHaveAttribute("open")
      await page.keyboard.press("Space")
      await expect(accordion).toHaveAttribute("open", "")
      for (const format of ["Table format", "Card format"]) {
        await page.getByRole("radio", { name: format, exact: true }).click()
        await expect(accordion).toHaveAttribute("open", "")
        expect(await count("Failed runs")).toBe(failed)
        expect(await count("Timed out runs")).toBe(timedOut)
      }
      expect(
        await page.evaluate(
          () => document.documentElement.scrollWidth > window.innerWidth
        )
      ).toBe(false)
      const accessibility = await new AxeBuilder({ page })
        .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
        .analyze()
      expect(accessibility.violations).toEqual([])
      expect(errors).toEqual([])
    })
  }
}
