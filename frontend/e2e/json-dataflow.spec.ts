import AxeBuilder from "@axe-core/playwright"
import { expect, test } from "@playwright/test"
import { z } from "zod"

// Read-only against four already-triggered, synthetic-only demo runs.
const runs = z
  .array(
    z.object({
      mode: z.enum(["success", "retry", "failure", "random"]),
      runId: z.uuid(),
    })
  )
  .length(4)
  .parse(JSON.parse(process.env.WF_JSON_DATAFLOW_RUNS ?? "[]"))
const title = "Synthetic JSON dataflow and failure report"

for (const { mode, runId } of runs) {
  test(`${mode}: real stored JSON, failed-attempt data and recorded graph retries`, async ({
    page,
  }) => {
    const snapshotResponse = await page.request.get(`/api/v1/runs/${runId}`)
    expect(snapshotResponse.ok()).toBe(true)
    const snapshot = await snapshotResponse.json()
    expect(snapshot.workflowId).toBe("demo/release/json-failure-demo")
    expect(snapshot.status).toBe(mode === "success" || mode === "retry" ? 3 : 4)
    expect(snapshot.nodes.report).toBe(5)
    const retries = mode === "success" ? 0 : mode === "failure" ? 1 : 2
    if (mode !== "success") expect(snapshot.nodes["optional-check"]).toBe(6)
    if (mode === "failure" || mode === "random")
      expect(snapshot.nodes.validate).toBe(7)

    const indexResponse = await page.request.get(
      `/api/v1/runs/${runId}/reports`
    )
    expect(indexResponse.ok()).toBe(true)
    const index = await indexResponse.json()
    expect(index.items).toHaveLength(1)
    const report = index.items[0]
    expect(report.kind).toBe("json")
    expect(report.title).toBe(title)
    const contentResponse = await page.request.get(
      `/api/v1/reports/${report.id}/content`
    )
    expect(contentResponse.ok()).toBe(true)
    expect(contentResponse.headers()["cache-control"]).toContain("no-store")
    const bytes = await contentResponse.body()
    expect(bytes.byteLength).toBe(report.sizeBytes)
    const content = JSON.parse(bytes.toString("utf8"))
    expect(content.runId).toBe(runId)
    expect(content.scenario.mode).toBe(mode)
    expect(content.scenario.seed).toBe(42)
    expect(content.fetchAttempts).toBe(retries + 1)
    expect(content.expectedRequiredOutcome).toBe(
      mode === "success" || mode === "retry" ? "Succeeded" : "Failed"
    )

    await page.goto(`/runs/${runId}`)
    const node = page.getByRole("button", {
      name: "fetch: Succeeded",
      exact: true,
    })
    await expect(node).toHaveAccessibleDescription(
      new RegExp(`Recorded retries: ${retries}`)
    )
    if (retries)
      await expect(
        node.getByTitle(
          `${retries} recorded ${retries === 1 ? "retry" : "retries"}`
        )
      ).toBeVisible()
    await node.click()
    const inspector = page.getByRole("region", { name: "Selected node fetch" })
    await inspector
      .getByText("Inspect recorded inputs / outputs", { exact: true })
      .click()
    const selection = inspector.getByLabel("Output attempt")
    await expect(selection).toHaveValue(
      JSON.stringify(["fetch/request", retries + 1])
    )
    await expect(selection.locator("option")).toHaveCount(retries + 1)
    const tree = inspector.getByLabel("Attempt JSON outputs")
    await tree.getByText(/^"request": Object/).click()
    await tree.getByText(/^"inputs": Object/).click()
    await tree.getByText(/^"outputs": Object/).click()
    await expect(tree.getByText('"mode":', { exact: true })).toBeVisible()
    await expect(tree.getByText('"Succeeded"', { exact: true })).toBeVisible()
    if (retries) {
      await selection.selectOption(JSON.stringify(["fetch/request", 1]))
      await tree.getByText(/^"request": Object/).click()
      await tree.getByText(/^"outputs": Object/).click()
      await expect(tree.getByText('"Failed"', { exact: true })).toBeVisible()
      await expect(
        tree.getByText('"attempt":', { exact: true }).locator("..")
      ).toHaveText('"attempt": 1')
    }
    if (mode === "failure" || mode === "random") {
      await page
        .getByRole("button", { name: "validate: Failed", exact: true })
        .click()
      const failed = page.getByRole("region", {
        name: "Selected node validate",
      })
      await failed
        .getByText("Inspect recorded inputs / outputs", { exact: true })
        .click()
      const failureTree = failed.getByLabel("Attempt JSON outputs")
      await failureTree.getByText(/^"total": Object/).click()
      await failureTree.getByText(/^"outputs": Object/).click()
      await expect(
        failureTree.getByText('"Failed"', { exact: true })
      ).toBeVisible()
    }
    await page.goto(`/runs/${runId}/reports/${report.id}`)
    await expect(
      page.getByRole("heading", { name: title, exact: true })
    ).toBeVisible()
    await page.getByText(/^"scenario": Object/).click()
    await expect(page.getByText('"mode":', { exact: true })).toBeVisible()
    await expect(
      page.getByText(JSON.stringify(mode), { exact: true })
    ).toBeVisible()
    await expect(page.getByRole("alert")).toHaveCount(0)
  })
}

for (const width of [320, 768, 1440]) {
  test(`failed-node inspector remains accessible without page overflow at ${width}px`, async ({
    page,
  }) => {
    const run = runs.find((item) => item.mode === "failure")!
    await page.setViewportSize({ width, height: 1000 })
    await page.goto(`/runs/${run.runId}`)
    await page
      .getByRole("button", { name: "validate: Failed", exact: true })
      .click()
    const inspector = page.getByRole("region", {
      name: "Selected node validate",
    })
    await inspector
      .getByText("Inspect recorded inputs / outputs", { exact: true })
      .click()
    await expect(inspector.getByLabel("Attempt JSON outputs")).toBeVisible()
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth)
    ).toBeLessThanOrEqual(width)
    const accessibility = await new AxeBuilder({ page })
      .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
      .analyze()
    expect(accessibility.violations).toEqual([])
  })
}
