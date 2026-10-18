import { createHash, randomUUID } from "node:crypto"
import AxeBuilder from "@axe-core/playwright"
import {
  expect,
  test,
  type APIRequestContext,
  type Page,
} from "@playwright/test"
import { z } from "zod"
import { versionPlanSchema } from "../src/features/taskplan/api"

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

async function publish(
  request: APIRequestContext,
  workflowId: string,
  version: string,
  sha256: string,
  size: number
) {
  const policy = {
    attemptTimeout: "00:00:12",
    autoRetry: true,
    retry: {
      retries: 2,
      on: ["TaskError", "Timeout"],
      backoff: { kind: "Exponential", delay: "00:00:01", maxDelay: "00:00:08" },
    },
  }
  const response = await request.post("/api/v1/registrations", {
    data: {
      gitCommit: (version === "1.0.0" ? "a" : "b").repeat(40),
      gitAuthor: "Task Plan integration",
      workflowId,
      package: { sha256, size },
      source: "// source-private-value",
      manifest: {
        sdkVersion: "1.0.0",
        name: workflowId,
        version,
        metadata: { type: "release", values: {} },
        variables: [
          {
            key: "private",
            type: "String",
            required: false,
            default: "plan-default-secret",
            allowedValues: [],
          },
        ],
        runTimeout: "01:00:00",
        stages: [],
        nodes: [
          {
            key: "prepare",
            dependencies: [],
            tasks: [],
            mode: "Series",
            failure: "Hard",
            inputs: [],
            timeout: null,
            maxParallel: null,
          },
          {
            key: "work",
            dependencies: [{ node: "prepare", when: "OnSuccess" }],
            mode: "Series",
            failure: "Hard",
            inputs: [],
            timeout: "00:01:00",
            maxParallel: null,
            placement: {
              selector: {
                os: { literal: "linux", variable: null },
                pool: null,
                labels: [],
                machines: [],
              },
              affinity: { kind: "Node", nodeKey: null },
            },
            tasks: [
              {
                key: "sdk",
                failure: "Hard",
                policy,
                action: {
                  kind: "SdkFunction",
                  declaration:
                    version === "1.0.0"
                      ? "Example.Deploy.DeclaredSdkFunction"
                      : "Example.Deploy.NewSdkFunction",
                },
              },
              {
                key: "command",
                failure: "Soft",
                policy,
                action: { kind: "Command", declaration: "dotnet" },
              },
              { key: "legacy", failure: "Hard", policy },
            ],
          },
        ],
      },
    },
  })
  expect(response.status(), await response.text()).toBe(201)
  return z.object({ workflowVersion: z.uuid() }).parse(await response.json())
    .workflowVersion
}

test("run link reads its immutable Task Plan after a newer publish without exposing private values", async ({
  page,
  request,
}) => {
  const workflowId = `ui-tests/release/plan-${randomUUID()}`
  const bytes = Buffer.from(`Task Plan package ${workflowId}`)
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
  const triggered = await request.post("/api/v1/runs", {
    data: {
      workflowId,
      version: first,
      variables: { private: "run-private-secret" },
    },
  })
  expect(triggered.status(), await triggered.text()).toBe(202)
  const run = z.object({ runId: z.uuid() }).parse(await triggered.json()).runId
  const second = await publish(
    request,
    workflowId,
    "2.0.0",
    sha256,
    bytes.length
  )
  expect(second).not.toBe(first)
  const planResponses: string[] = []
  const mutations: string[] = []
  page.on("request", (request) => {
    const path = new URL(request.url()).pathname
    if (path.startsWith("/api/") && request.method() !== "GET")
      mutations.push(path)
  })
  await page.goto(`/runs/${run}`)
  const link = page.getByRole("link", { name: "Task Plan for this run" })
  await expect(link).toHaveAttribute(
    "href",
    `/t/ui-tests/release/w/${workflowId.split("/")[2]}/versions/${first}`
  )
  await expect(
    page.getByRole("region", { name: "Live run graph", exact: true })
  ).toBeVisible()
  page.on("response", async (response) => {
    if (
      new URL(response.url()).pathname ===
      `/api/v1/workflows/${workflowId}/versions/${first}`
    )
      planResponses.push(await response.text())
  })
  await link.click()
  await expect(
    page.getByRole("heading", { name: "Task Plan", exact: true })
  ).toBeVisible()
  await expect(
    page.getByText("SdkFunction: Example.Deploy.DeclaredSdkFunction", {
      exact: true,
    })
  ).toBeVisible()
  await expect(page.getByText("Command: dotnet", { exact: true })).toBeVisible()
  await expect(
    page.getByText("Declaration unavailable in this historical manifest.", {
      exact: true,
    })
  ).toBeVisible()
  const work = page.getByRole("region", { name: "Node work" })
  await expect(work).toContainText("Dependencies: prepare (OnSuccess)")
  await expect(work).toContainText("OS: linux; Affinity: Node")
  await expect(work).toContainText("retry budget: 2")
  await expect(work).toContainText("Attempt timeout: 00:00:12")
  await expect(
    page.getByText("SdkFunction: Example.Deploy.NewSdkFunction", {
      exact: true,
    })
  ).toHaveCount(0)
  await accessible(page)
  await page.getByRole("button", { name: "Dark theme" }).click()
  await page.setViewportSize({ width: 375, height: 812 })
  await accessible(page)
  await expect.poll(() => planResponses.length).toBe(1)
  const body = planResponses[0]!
  const plan = versionPlanSchema.parse(JSON.parse(body))
  expect(plan.versionId).toBe(first)
  expect(plan.version).toBe("1.0.0")
  for (const secret of [
    "plan-default-secret",
    "run-private-secret",
    "source-private-value",
    '"variables"',
    '"inputs"',
    "packageUri",
    "sourceUri",
  ]) {
    expect(body).not.toContain(secret)
    await expect(page.locator("body")).not.toContainText(secret)
  }
  expect(mutations.filter((path) => !path.startsWith("/hubs/"))).toEqual([])
  const base = `/t/ui-tests/release/w/${workflowId.split("/")[2]}/versions/${first}`
  await page.goto(`${base}/tasks/${encodeURIComponent("work/sdk")}`)
  await expect(page.locator('[aria-current="true"]')).toContainText(
    "DeclaredSdkFunction"
  )
  await page.reload()
  await expect(page.locator('[aria-current="true"]')).toContainText(
    "DeclaredSdkFunction"
  )
  await page.goto(`${base}/tasks/${encodeURIComponent("work/absent")}`)
  await expect(page.getByRole("alert")).toHaveText(
    "This task is not part of the pinned version."
  )
})
