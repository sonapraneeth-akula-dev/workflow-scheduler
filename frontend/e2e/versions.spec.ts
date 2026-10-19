import { execFileSync } from "node:child_process"
import { createHash, randomUUID } from "node:crypto"
import { readFile, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { resolve } from "node:path"
import AxeBuilder from "@axe-core/playwright"
import {
  expect,
  test,
  type APIRequestContext,
  type Page,
} from "@playwright/test"
import { z } from "zod"

async function accessible(page: Page) {
  const result = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
    .analyze()
  expect(
    result.violations.filter(
      (issue) => issue.impact === "critical" || issue.impact === "serious"
    )
  ).toEqual([])
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth
    )
  ).toBe(true)
}

async function packageUpload(request: APIRequestContext, workflowId: string) {
  const bytes = Buffer.from(`Versions browser fixture ${workflowId}`)
  const sha256 = createHash("sha256").update(bytes).digest("hex")
  const response = await request.post("/api/v1/upload-grants", {
    data: { kind: "package", sha256, size: bytes.length },
  })
  expect(response.status(), await response.text()).toBe(201)
  const grant = z.object({ uploadUri: z.string() }).parse(await response.json())
  const upload = await request.put(grant.uploadUri, { data: bytes })
  expect(upload.status(), await upload.text()).toBe(201)
  return { sha256, size: bytes.length }
}

async function publish(
  request: APIRequestContext,
  workflowId: string,
  version: string,
  compiled: { sha256: string; size: number },
  changed = false
) {
  const source = `// ${workflowId} ${version}\r\n// ${changed ? "modified" : "original"} <script>window.__versionsXss = true</script>\r\n// \u03bb\n`
  const policy = {
    attemptTimeout: "00:00:12",
    autoRetry: true,
    retry: {
      retries: changed ? 2 : 1,
      on: ["TaskError", "Timeout"],
      backoff: { kind: "Fixed", delay: "00:00:01", maxDelay: "00:01:00" },
    },
  }
  const node = (key: string) => ({
    key,
    dependencies: [],
    tasks: [
      {
        key: "execute",
        policy,
        failure: "Hard",
        action: { kind: "SdkFunction", declaration: `BrowserFixture.${key}` },
      },
    ],
    mode: "Series",
    failure: "Hard",
    inputs: [],
    timeout: null,
    maxParallel: null,
  })
  const body = {
    gitCommit: (changed ? "b" : "a").repeat(40),
    gitAuthor: "Version author <author@example.test>",
    changeNote: changed
      ? "Changed <script>window.__versionsXss = true</script>"
      : null,
    workflowId,
    package: compiled,
    source,
    manifest: {
      sdkVersion: "1.0.0",
      name: workflowId,
      version,
      metadata: { type: "release", values: {} },
      variables: [
        {
          key: "changed",
          type: "String",
          required: false,
          default: changed ? "new literal" : "old literal",
          allowedValues: [],
        },
        changed
          ? {
              key: "added",
              type: "Long",
              required: false,
              default: "EXACT_MAX_LONG",
              allowedValues: [],
            }
          : {
              key: "removed",
              type: "String",
              required: false,
              default: "old",
              allowedValues: [],
            },
      ],
      runTimeout: changed ? "02:00:00" : "01:00:00",
      stages: [],
      nodes: [node("work"), node(changed ? "added" : "removed")],
    },
  }
  const response = await request.post("/api/v1/registrations", {
    headers: { "Content-Type": "application/json" },
    data: JSON.stringify(body).replace(
      '"EXACT_MAX_LONG"',
      "9223372036854775807"
    ),
  })
  expect(response.status(), await response.text()).toBe(201)
  return {
    id: z.object({ workflowVersion: z.uuid() }).parse(await response.json())
      .workflowVersion,
    source,
  }
}

test("versions expose immutable provenance, lazy source and manifest diffs, corruption recovery and pinned run links", async ({
  page,
  request,
}) => {
  test.setTimeout(120_000)
  const workflowId = `ui-tests/release/versions-${randomUUID()}`
  const compiled = await packageUpload(request, workflowId)
  const before = await publish(request, workflowId, "1.0.0", compiled)
  const runResponse = await request.post("/api/v1/runs", {
    data: { workflowId, version: before.id },
  })
  expect(runResponse.status(), await runResponse.text()).toBe(202)
  const run = z.object({ runId: z.uuid() }).parse(await runResponse.json())
  const after = await publish(request, workflowId, "2.0.0", compiled, true)
  const route = `/t/ui-tests/release/w/${workflowId.split("/")[2]}/versions`
  const diffRequests: string[] = []
  const monacoRequests: string[] = []
  const mutations: string[] = []
  page.on("request", (request) => {
    const url = new URL(request.url())
    if (url.pathname.endsWith("/diff")) diffRequests.push(request.url())
    if (url.pathname.includes("monaco-editor"))
      monacoRequests.push(url.pathname)
    if (url.pathname.startsWith("/api/") && request.method() !== "GET")
      mutations.push(url.pathname)
  })
  await page.goto(route)
  await expect(
    page.getByRole("heading", { name: "Versions", exact: true })
  ).toBeVisible()
  const table = page.getByRole("region", { name: "Version history table" })
  await expect(
    table.getByRole("link", { name: "2.0.0", exact: true })
  ).toHaveAttribute("href", `${route}/${after.id}`)
  await expect(
    table.getByText("Version author <author@example.test>", { exact: true })
  ).toHaveCount(2)
  await expect(
    table.getByText("No change note.", { exact: true })
  ).toBeVisible()
  await expect(
    table.getByText("Changed <script>window.__versionsXss = true</script>", {
      exact: true,
    })
  ).toBeVisible()
  expect(diffRequests).toEqual([])
  expect(monacoRequests).toEqual([])
  expect(
    await page.evaluate(() => Object.hasOwn(window, "__versionsXss"))
  ).toBe(false)
  await accessible(page)

  const downloadReady = page.waitForEvent("download")
  await table
    .getByRole("button", {
      name: `Download source for version 1.0.0 (${before.id})`,
    })
    .focus()
  await page.keyboard.press("Enter")
  const download = await downloadReady
  expect(download.suggestedFilename()).toBe("workflow.cs")
  const downloadedPath = await download.path()
  if (!downloadedPath) throw new Error("Historical source was not downloaded.")
  expect(await readFile(downloadedPath, "utf8")).toBe(before.source)
  expect(diffRequests).toHaveLength(1)
  expect(new URL(diffRequests[0]!).searchParams.get("from")).toBe(before.id)
  expect(new URL(diffRequests[0]!).searchParams.get("to")).toBe(before.id)
  expect(monacoRequests).toEqual([])
  await page
    .getByText("Version download and comparison example", { exact: true })
    .click()
  await expect(
    page.getByText(/These example versions are not published data/)
  ).toBeVisible()
  await page
    .getByRole("button", {
      name: "Compare latest two registrations",
      exact: true,
    })
    .click()
  const comparison = page.getByRole("region", {
    name: "Version comparison",
    exact: true,
  })
  await expect(
    comparison.getByText("Changed: work", { exact: true })
  ).toBeVisible()
  await expect(
    comparison.getByText("Added: added", { exact: true })
  ).toHaveCount(2)
  await expect(
    comparison.getByText("Removed: removed", { exact: true })
  ).toHaveCount(2)
  await comparison
    .getByRole("region", { name: "variables changes", exact: true })
    .getByText("Added: added", { exact: true })
    .click()
  await expect(
    comparison.getByRole("region", {
      name: "variables added after",
      exact: true,
    })
  ).toContainText("9223372036854775807")
  await expect(page.locator(".monaco-diff-editor")).toBeVisible()
  await expect(
    page.getByRole("textbox", {
      name: "Workflow source: modified text",
      exact: true,
    })
  ).toHaveAttribute("readonly", "true")
  const plain = page.getByText("Plain-text diff alternatives", { exact: true })
  await plain.click()
  const texts = comparison
    .locator("details")
    .filter({ has: plain })
    .locator("pre")
  await expect(texts.nth(0)).toHaveText(before.source)
  await expect(texts.nth(1)).toHaveText(after.source)
  expect(diffRequests).toHaveLength(2)
  expect(new URL(diffRequests[1]!).searchParams.get("from")).toBe(before.id)
  expect(new URL(diffRequests[1]!).searchParams.get("to")).toBe(after.id)
  expect(
    await page.evaluate(() => Object.hasOwn(window, "__versionsXss"))
  ).toBe(false)
  await accessible(page)

  const fixture = z
    .object({
      directory: z.string().regex(/^wf-ui-[a-zA-Z0-9_-]+$/),
      container: z.string().regex(/^wf-ui-[a-f0-9-]+$/),
    })
    .parse(
      JSON.parse(
        await readFile(new URL("../.test-stack.json", import.meta.url), "utf8")
      )
    )
  const hash = execFileSync(
    "docker",
    [
      "exec",
      fixture.container,
      "psql",
      "-U",
      "workflow",
      "-d",
      "workflow",
      "-At",
      "-c",
      `SELECT encode(source_sha256,'hex') FROM workflow_versions WHERE id='${after.id}'`,
    ],
    { encoding: "utf8" }
  ).trim()
  z.string()
    .regex(/^[a-f0-9]{64}$/)
    .parse(hash)
  const sourcePath = resolve(tmpdir(), fixture.directory, "sources", hash)
  const saved = await readFile(sourcePath)
  try {
    const broken = Buffer.from(saved)
    broken[0] = broken[0]! ^ 0xff
    await writeFile(sourcePath, broken)
    await comparison.getByRole("button", { name: "Refresh comparison" }).click()
    await expect(comparison.getByRole("alert")).toContainText("inconsistent")
    await expect(
      comparison.getByText("Changed: work", { exact: true })
    ).toHaveCount(0)
    await expect(
      comparison.getByRole("textbox", {
        name: "Workflow source: modified text",
        exact: true,
      })
    ).toHaveCount(0)
    await expect(
      comparison.getByText("No source changes.", { exact: true })
    ).toHaveCount(0)
  } finally {
    await writeFile(sourcePath, saved)
  }
  await comparison.getByRole("button", { name: "Refresh comparison" }).click()
  await expect(page.locator(".monaco-diff-editor")).toBeVisible()
  await expect(
    comparison.getByRole("textbox", {
      name: "Workflow source: modified text",
      exact: true,
    })
  ).toHaveAttribute("readonly", "true")

  await page
    .getByRole("combobox", { name: "Compare from", exact: true })
    .selectOption(after.id)
  await page
    .getByRole("combobox", { name: "Compare to", exact: true })
    .selectOption(before.id)
  await page
    .getByRole("button", { name: "Compare versions", exact: true })
    .click()
  await expect(
    comparison.getByText("Version 2.0.0 compared with 1.0.0.", { exact: true })
  ).toBeVisible()
  await page
    .getByRole("combobox", { name: "Compare to", exact: true })
    .selectOption(after.id)
  await page
    .getByRole("button", { name: "Compare versions", exact: true })
    .click()
  await expect(comparison.getByText(/No source changes\./)).toBeVisible()
  await page.setViewportSize({ width: 390, height: 844 })
  await page.getByRole("button", { name: "Dark theme" }).click()
  const compareFrom = page.getByRole("combobox", {
    name: "Compare from",
    exact: true,
  })
  const compareTo = page.getByRole("combobox", {
    name: "Compare to",
    exact: true,
  })
  await compareFrom.focus()
  await page.keyboard.press("Tab")
  await expect(compareTo).toBeFocused()
  await expect
    .poll(() =>
      compareTo.evaluate(
        (element) =>
          element.matches(":focus-visible") &&
          getComputedStyle(element).boxShadow.includes("0px 0px 0px 3px")
      )
    )
    .toBe(true)
  await accessible(page)

  await table.getByRole("link", { name: "1.0.0", exact: true }).click()
  await expect(
    page.getByRole("heading", { name: "Task Plan", exact: true })
  ).toBeVisible()
  await expect(
    page.getByText(`Pinned version: ${before.id}`, { exact: true })
  ).toBeVisible()
  await expect(page.getByText("No change note.", { exact: true })).toHaveCount(
    2
  )
  await expect(page.getByText("Git author", { exact: true })).toBeVisible()
  await accessible(page)
  await page.goto(`/runs/${run.runId}`)
  await expect(
    page.getByRole("link", { name: "Task Plan for this run" })
  ).toHaveAttribute("href", `${route}/${before.id}`)
  expect(mutations).toEqual([])
})

test("versions continue real keyset pages without duplication after a newer publish", async ({
  page,
  request,
}) => {
  test.setTimeout(120_000)
  const workflowId = `ui-tests/release/versions-pages-${randomUUID()}`
  const compiled = await packageUpload(request, workflowId)
  const ids: string[] = []
  for (let index = 0; index < 51; index++)
    ids.push((await publish(request, workflowId, `1.0.${index}`, compiled)).id)
  const route = `/t/ui-tests/release/w/${workflowId.split("/")[2]}/versions`
  await page.goto(route)
  const table = page.getByRole("region", { name: "Version history table" })
  await expect(table.getByRole("row")).toHaveCount(51)
  await expect(
    table.getByRole("link", { name: "1.0.50", exact: true })
  ).toBeVisible()
  await publish(request, workflowId, "1.0.51", compiled)
  await page.getByRole("button", { name: "Load more versions" }).click()
  await expect(table.getByRole("row")).toHaveCount(52)
  await expect(
    table.getByRole("link", { name: "1.0.0", exact: true })
  ).toHaveAttribute("href", `${route}/${ids[0]}`)
  await expect(
    table.getByRole("link", { name: "1.0.51", exact: true })
  ).toHaveCount(0)
  await expect(
    page.getByRole("button", { name: "Load more versions" })
  ).toHaveCount(0)
  const hrefs = await table
    .getByRole("link")
    .evaluateAll((links) => links.map((link) => link.getAttribute("href")))
  expect(new Set(hrefs).size).toBe(51)
  await page.getByRole("button", { name: "Refresh versions" }).click()
  await expect(
    table.getByRole("link", { name: "1.0.51", exact: true })
  ).toBeVisible()
})
