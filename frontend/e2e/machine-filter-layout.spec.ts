import { expect, test } from "@playwright/test"

test("machine filter labels stay top-aligned beside the multiline field", async ({
  page,
}) => {
  await page.setViewportSize({ width: 2000, height: 1000 })
  await page.goto("/machines")
  const form = page.getByRole("form", { name: "Machine filters" })
  await expect(form).toBeVisible()
  const tops = await form.locator(":scope > label").evaluateAll((labels) =>
    labels.map((label) => label.getBoundingClientRect().top)
  )
  expect(tops).toHaveLength(4)
  expect(Math.max(...tops) - Math.min(...tops)).toBeLessThanOrEqual(1)
})
