import { expect, test } from "@playwright/test"
import { openTeamRunList } from "./team-runs"

test("known catalogue values and custom entries both survive run search and reload", async ({
  page,
}) => {
  await openTeamRunList(page)
  const form = page.getByRole("form", { name: "Run filters" })
  const type = form.getByLabel("Type", { exact: true })
  const typesId = await type.getAttribute("list")
  const knownType = form.locator(`datalist[id="${typesId}"] option`).first()
  await expect(knownType).toBeAttached()
  const typeName = (await knownType.getAttribute("value"))!
  await type.fill(typeName)
  const workflow = form.getByLabel("Workflow ID")
  const workflowId = await workflow.getAttribute("list")
  const knownWorkflow = form
    .locator(`datalist[id="${workflowId}"] option`)
    .first()
  await expect(knownWorkflow).toBeAttached()
  const workflowName = (await knownWorkflow.getAttribute("value"))!
  await workflow.fill(workflowName)
  await form.getByRole("button", { name: "Search runs" }).click()
  await page.reload()
  await expect(type).toHaveValue(typeName)
  await expect(workflow).toHaveValue(workflowName)
  await form.getByText("Advanced filters", { exact: true }).click()
  const key = form.getByLabel("Metadata key", { exact: true })
  const keysId = await key.getAttribute("list")
  const knownKey = form.locator(`datalist[id="${keysId}"] option`).first()
  await expect(knownKey).toBeAttached()
  const keyName = (await knownKey.getAttribute("value"))!
  await key.fill(keyName)
  await form.getByLabel("Metadata value").fill(" custom & exact ")
  await form.getByRole("button", { name: "Add metadata filter" }).click()
  const expectedMetadata = JSON.stringify({ [keyName]: " custom & exact " })
  await expect(form.getByLabel("Metadata filters (JSON)")).toHaveValue(
    expectedMetadata
  )
  await type.fill("custom-type")
  await expect(workflow).toHaveValue("")
  const team = decodeURIComponent(new URL(page.url()).pathname.split("/")[2]!)
  const customWorkflow = `${team}/custom-type/custom-workflow`
  await workflow.fill(customWorkflow)
  const request = page.waitForRequest((request) => {
    const url = new URL(request.url())
    return (
      url.pathname === "/api/v1/runs" &&
      url.searchParams.get("workflowId") === customWorkflow
    )
  })
  await form.getByRole("button", { name: "Search runs" }).click()
  const query = new URL((await request).url()).searchParams
  expect(query.get("team")).toBe(team)
  expect(query.get(`meta.${keyName}`)).toBe(" custom & exact ")
  await page.reload()
  await expect(type).toHaveValue("custom-type")
  await expect(workflow).toHaveValue(customWorkflow)
  await expect(form.getByLabel("Metadata filters (JSON)")).toHaveValue(
    expectedMetadata
  )
})
