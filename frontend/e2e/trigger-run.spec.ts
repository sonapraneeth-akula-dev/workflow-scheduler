import { spawnSync } from "node:child_process"
import { createHash, randomUUID } from "node:crypto"
import { readFile } from "node:fs/promises"
import AxeBuilder from "@axe-core/playwright"
import { expect, test, type APIRequestContext } from "@playwright/test"
import { z } from "zod"
import { parseJson } from "../src/lib/json"
import { triggerSchema } from "../src/features/runs/variables"

async function publish(
  request: APIRequestContext,
  workflowId: string,
  version: string,
  sha256: string,
  size: number
) {
  const variables = [
    {
      key: "name",
      type: "String",
      required: true,
      default: "default name",
      allowedValues: [],
    },
    {
      key: "count",
      type: "Int",
      required: true,
      default: 3,
      allowedValues: [],
    },
    {
      key: "sequence",
      type: "Long",
      required: true,
      default: JSON.rawJSON("9223372036854775807"),
      allowedValues: [],
    },
    {
      key: "ratio",
      type: "Double",
      required: true,
      default: 0.25,
      allowedValues: [],
    },
    {
      key: "enabled",
      type: "Bool",
      required: true,
      default: false,
      allowedValues: [],
    },
    {
      key: "mode",
      type: "Enum",
      required: true,
      default: "Safe",
      allowedValues: ["Safe", "Fast"],
    },
    {
      key: "names",
      type: "StringList",
      required: true,
      default: ["one", ""],
      allowedValues: [],
    },
    {
      key: "timeout",
      type: "Duration",
      required: true,
      default: "00:01:00",
      allowedValues: [],
    },
    {
      key: "credential",
      type: "Secret",
      required: true,
      default: { Name: "secret://local/test-token" },
      allowedValues: [],
    },
    {
      key: "optional",
      type: "String",
      required: false,
      default: null,
      allowedValues: [],
    },
  ].map((variable) => {
    if (variable.key !== "optional") return variable
    return {
      key: variable.key,
      type: variable.type,
      required: false,
      allowedValues: [],
    }
  })
  const response = await request.post("/api/v1/registrations", {
    headers: { "Content-Type": "application/json" },
    data: JSON.stringify({
      gitCommit: (version === "1.0.0" ? "a" : "b").repeat(40),
      gitAuthor: "Workflow trigger integration",
      workflowId,
      package: { sha256, size },
      source: "// trigger fixture; no executable credentials",
      manifest: {
        sdkVersion: "1.0.0",
        name: workflowId,
        version,
        metadata: { type: "release", values: {} },
        variables,
        runTimeout: "01:00:00",
        stages: [],
        nodes: [
          {
            key: "work",
            dependencies: [],
            tasks: [],
            mode: "Series",
            failure: "Hard",
            inputs: [],
          },
        ],
      },
    }),
  })
  expect(response.status(), await response.text()).toBe(201)
  return z.object({ workflowVersion: z.uuid() }).parse(await response.json())
    .workflowVersion
}

test("workflow form validates all types and submits exact defaults/overrides to a captured immutable version", async ({
  page,
  request,
}, info) => {
  test.setTimeout(90_000)
  const name = `trigger-${info.project.name}-${randomUUID()}`
  const workflowId = `ui-tests/release/${name}`
  const bytes = Buffer.from(`Trigger package ${workflowId}`)
  const sha256 = createHash("sha256").update(bytes).digest("hex")
  const grantResponse = await request.post("/api/v1/upload-grants", {
    data: { kind: "package", sha256, size: bytes.length },
  })
  expect(grantResponse.status()).toBe(201)
  const grant = z
    .object({ uploadUri: z.string() })
    .parse(await grantResponse.json())
  expect((await request.put(grant.uploadUri, { data: bytes })).status()).toBe(
    201
  )
  const first = await publish(
    request,
    workflowId,
    "1.0.0",
    sha256,
    bytes.length
  )
  await page.goto(`/t/ui-tests/release/w/${name}/history`)
  await page.getByRole("button", { name: "Trigger run", exact: true }).click()
  const dialog = page.getByRole("dialog", { name: "Trigger workflow run" })
  await expect(dialog).toBeVisible()
  await expect(dialog.getByLabel("sequence", { exact: true })).toHaveValue(
    "9223372036854775807"
  )
  await expect(dialog.getByLabel("name", { exact: true })).toHaveValue(
    "default name"
  )
  await expect(dialog.getByLabel("credential", { exact: true })).toHaveValue(
    "secret://local/test-token"
  )
  await expect(dialog.getByLabel("optional", { exact: true })).toBeDisabled()
  const second = await publish(
    request,
    workflowId,
    "2.0.0",
    sha256,
    bytes.length
  )
  expect(second).not.toBe(first)
  await expect(dialog.getByText(/Pinned version:/)).toContainText(first)
  const invalid = [
    ["count", "2147483648", "42"],
    ["sequence", "9223372036854775808", "-9223372036854775808"],
    ["ratio", "1e999", "1.5"],
    ["names", '["one",2]', '["one","two",""]'],
    ["timeout", "00:60:00", "1.02:03:04.1234567"],
    ["credential", "literal secret!", "secret://local/test-token"],
  ]
  for (const [key, bad, good] of invalid) {
    const input = dialog.getByLabel(key!, { exact: true })
    await input.fill(bad!)
    await expect(input).toHaveAttribute("aria-invalid", "true")
    await expect(
      dialog.getByRole("button", { name: "Accept run", exact: true })
    ).toBeDisabled()
    await expect(
      dialog.locator(`[id="${await input.getAttribute("id")}-error"]`)
    ).toBeVisible()
    await input.fill(good!)
    await expect(input).toHaveAttribute("aria-invalid", "false")
  }
  await dialog
    .getByLabel("name", { exact: true })
    .fill("exact browser override")
  await dialog
    .getByLabel("enabled", { exact: true })
    .selectOption({ label: "true" })
  await dialog
    .getByLabel("mode", { exact: true })
    .selectOption({ label: "Fast" })
  await dialog
    .getByRole("checkbox", { name: "Set optional", exact: true })
    .check()
  await dialog.getByLabel("optional", { exact: true }).fill("temporary")
  await dialog
    .getByRole("checkbox", { name: "Set optional", exact: true })
    .uncheck()
  for (const dark of [false, true]) {
    await page.setViewportSize(
      dark ? { width: 375, height: 812 } : { width: 1440, height: 1080 }
    )
    await page.evaluate(
      (dark) => document.documentElement.classList.toggle("dark", dark),
      dark
    )
    const axe = await new AxeBuilder({ page })
      .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
      .analyze()
    expect(
      axe.violations.filter(
        (issue) => issue.impact === "critical" || issue.impact === "serious"
      )
    ).toEqual([])
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth
      )
    ).toBe(true)
    expect(
      await dialog.evaluate(
        (element) => element.scrollWidth <= element.clientWidth
      )
    ).toBe(true)
  }
  const accepted = page.waitForResponse(
    (response) =>
      response.request().method() === "POST" &&
      new URL(response.url()).pathname === "/api/v1/runs"
  )
  await dialog.getByRole("button", { name: "Accept run", exact: true }).focus()
  await page.keyboard.press("Enter")
  const response = await accepted
  expect(response.status(), await response.text()).toBe(202)
  const runId = z.object({ runId: z.uuid() }).parse(await response.json()).runId
  const submission = response.request()
  const body = submission.postData()!
  expect(body).toContain('"sequence":-9223372036854775808')
  expect(body).not.toContain('"optional"')
  expect(JSON.parse(body).version).toBe(first)
  const key = z.uuid().parse(submission.headers()["idempotency-key"])
  await expect(dialog.getByRole("status")).toContainText("accepted")
  await expect(
    dialog.getByRole("link", { name: runId, exact: true })
  ).toBeVisible()
  const replay = await request.post("/api/v1/runs", {
    headers: { "Content-Type": "application/json", "Idempotency-Key": key },
    data: body,
  })
  expect(replay.status()).toBe(202)
  expect(z.object({ runId: z.uuid() }).parse(await replay.json()).runId).toBe(
    runId
  )
  const fixture = z
    .object({ container: z.string().regex(/^wf-ui-[0-9a-f-]+$/) })
    .parse(
      JSON.parse(
        await readFile(new URL("../.test-stack.json", import.meta.url), "utf8")
      )
    )
  await expect
    .poll(() => {
      const result = spawnSync(
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
          `SELECT json_build_object('version',version_id,'variables',variables)::text FROM runs WHERE id='${runId}'`,
        ],
        { encoding: "utf8", timeout: 10_000 }
      )
      if (result.status !== 0)
        throw new Error(
          result.stderr ||
            result.error?.message ||
            "Trigger fixture database read failed."
        )
      return result.stdout.trim()
    })
    .toContain('"sequence": -9223372036854775808')
  const history = await request.get(
    `/api/v1/runs/history?workflowId=${encodeURIComponent(workflowId)}`
  )
  expect(history.status()).toBe(200)
  const rows = z
    .object({
      items: z.array(
        z.object({
          runId: z.uuid().nullable(),
          versionId: z.uuid().nullable(),
          trigger: z.string().nullable(),
        })
      ),
    })
    .parse(await history.json())
  expect(rows.items.filter((row) => row.runId === runId)).toEqual([
    { runId, versionId: first, trigger: "Manual" },
  ])
  await dialog.getByRole("button", { name: "Close", exact: true }).click()
  await expect(dialog).not.toBeVisible()
  await expect(
    page.getByRole("button", { name: "Trigger run", exact: true })
  ).toBeFocused()
  await page.goto(`/t/ui-tests/release/w/${name}/versions/${first}`)
  await page.getByRole("button", { name: "Trigger run", exact: true }).click()
  await expect(dialog.getByText(/Pinned version:/)).toContainText(first)
  const exact = await request.get(
    `/api/v1/workflows/ui-tests/release/${name}/variables?version=${first}`
  )
  expect(exact.status()).toBe(200)
  expect(triggerSchema.parse(parseJson(await exact.text())).versionId).toBe(
    first
  )
  await page.keyboard.press("Escape")
  await expect(dialog).not.toBeVisible()
})
