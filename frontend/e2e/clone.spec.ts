import { execFileSync } from "node:child_process"
import { createHash, randomUUID } from "node:crypto"
import AxeBuilder from "@axe-core/playwright"
import {
  expect,
  test,
  type APIRequestContext,
  type Page,
} from "@playwright/test"
import { z } from "zod"

async function accessible(page: Page) {
  await page.waitForFunction(() =>
    document
      .getAnimations()
      .every((animation) => animation.playState !== "running")
  )
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

async function fixture(request: APIRequestContext) {
  const team = `clone-${randomUUID()}`
  const workflow = `${team}/release/source`
  const packageBytes = Buffer.from(`clone fixture ${team}`)
  const sha256 = createHash("sha256").update(packageBytes).digest("hex")
  const grantResponse = await request.post("/api/v1/upload-grants", {
    data: { kind: "package", sha256, size: packageBytes.length },
  })
  expect(grantResponse.status(), await grantResponse.text()).toBe(201)
  const grant = z
    .object({ uploadUri: z.string() })
    .parse(await grantResponse.json())
  const upload = await request.put(grant.uploadUri, { data: packageBytes })
  expect(upload.status(), await upload.text()).toBe(201)
  async function publish(identity: string, version: string, marker: string) {
    const kind = identity.split("/")[1]!
    const source = `#:package Wf.Sdk@1.0.0
using Wf.Sdk;
var workflow = Workflow.Define("${identity}", "${version}", w => w.Type("${kind}")
    .Node("work", n => n.Task("echo", ctx => ctx.Outputs["message"] = "${marker}")));
return await workflow.RunAsync(args);
`
    const registered = await request.post("/api/v1/registrations", {
      data: {
        gitCommit: "a".repeat(40),
        gitAuthor: "Clone browser fixture",
        workflowId: identity,
        package: { sha256, size: packageBytes.length },
        source,
        registry: {
          team,
          types: ["release", "experiments"].map((name) => ({
            team,
            name,
            description: "Clone browser fixture",
            metadataSchema: { keys: [], allowFreeForm: true },
          })),
        },
        manifest: {
          sdkVersion: "1.0.0",
          name: identity,
          version,
          metadata: { type: kind, values: {} },
          variables: [],
          stages: [],
          runTimeout: "01:00:00",
          nodes: [
            {
              key: "work",
              dependencies: [],
              mode: "Series",
              failure: "Hard",
              inputs: [],
              timeout: null,
              maxParallel: null,
              tasks: [
                {
                  key: "echo",
                  failure: "Hard",
                  policy: {
                    attemptTimeout: "00:00:12",
                    autoRetry: true,
                    retry: {
                      retries: 1,
                      on: ["TaskError", "Timeout"],
                      backoff: {
                        kind: "Fixed",
                        delay: "00:00:01",
                        maxDelay: "00:01:00",
                      },
                    },
                  },
                  action: {
                    kind: "SdkFunction",
                    declaration: "CloneFixture.Echo",
                  },
                },
              ],
            },
          ],
        },
      },
    })
    expect(registered.status(), await registered.text()).toBe(201)
    return {
      id: z.object({ workflowVersion: z.uuid() }).parse(await registered.json())
        .workflowVersion,
      source,
    }
  }
  const historical = await publish(workflow, "1.0.0", "historical-clone-body")
  const latest = await publish(workflow, "2.0.0", "latest-do-not-clone")
  return {
    team,
    workflow,
    historical,
    latest,
    publish,
    path: `/api/v1/workflows/${team}/release/source`,
    route: `/t/${team}/release/w/source/versions`,
  }
}

async function snapshot(
  request: APIRequestContext,
  path: string,
  version: string
) {
  return Promise.all(
    [
      `${path}/versions/${version}`,
      `${path}/versions`,
      `${path}/diff?from=${version}&to=${version}`,
    ].map(async (url) => {
      const response = await request.get(url)
      expect(response.status(), await response.text()).toBe(200)
      return response.text()
    })
  )
}

function archiveFiles(path: string): Record<string, string> {
  const output = execFileSync(
    "powershell.exe",
    [
      "-NoProfile",
      "-NonInteractive",
      "-Command",
      `
    $ErrorActionPreference='Stop'
    [Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive=[System.IO.Compression.ZipFile]::OpenRead('${path.replaceAll("'", "''")}')
    try {
      $files=@{}
      foreach($entry in $archive.Entries) {
        $reader=[System.IO.StreamReader]::new($entry.Open())
        try { $files[$entry.FullName]=$reader.ReadToEnd() } finally { $reader.Dispose() }
      }
      ConvertTo-Json -InputObject $files -Depth 5 -Compress
    } finally { $archive.Dispose() }
  `,
    ],
    { encoding: "utf8" }
  )
  return z.record(z.string(), z.string()).parse(JSON.parse(output))
}

for (const viewport of [
  { name: "desktop", width: 1280, height: 900 },
  { name: "mobile", width: 390, height: 844 },
]) {
  for (const colorScheme of ["light", "dark"] as const) {
    test(`exact historical scaffold download, keyboard and axe: ${viewport.name} ${colorScheme}`, async ({
      page,
      request,
    }, testInfo) => {
      test.setTimeout(90_000)
      const seed = await fixture(request)
      const before = await snapshot(request, seed.path, seed.historical.id)
      await page.setViewportSize(viewport)
      await page.emulateMedia({ colorScheme })
      await page.goto(`${seed.route}/${seed.historical.id}`)
      await page.evaluate((theme) => {
        document.documentElement.classList.toggle("dark", theme === "dark")
      }, colorScheme)
      await expect(
        page.getByText(`Latest version: ${seed.latest.id}.`, { exact: true })
      ).toBeVisible()
      const trigger = page.getByRole("button", {
        name: `Clone version ${seed.historical.id}`,
        exact: true,
      })
      await trigger.focus()
      await page.keyboard.press("Enter")
      const dialog = page.getByRole("alertdialog", {
        name: "Clone workflow version",
      })
      await expect(dialog).toContainText(
        `${seed.workflow}@${seed.historical.id}`
      )
      await expect(dialog).not.toContainText(seed.latest.id)
      await dialog.getByLabel("New workflow name").fill("experiment")
      await dialog
        .getByLabel("Target team/type")
        .fill(`${seed.team}/experiments`)
      const submit = dialog.getByRole("button", {
        name: "Download scaffold",
        exact: true,
      })
      await expect(submit).toBeEnabled()
      await accessible(page)
      for (let index = 0; index < 6; index++) {
        await page.keyboard.press("Tab")
        expect(
          await dialog.evaluate((element) =>
            element.contains(document.activeElement)
          )
        ).toBe(true)
      }
      await page.screenshot({
        path: testInfo.outputPath("clone-dialog.png"),
        fullPage: true,
      })
      const clonePath = `${seed.path}/versions/${seed.historical.id}/clone`
      const [download, sent] = await Promise.all([
        page.waitForEvent("download"),
        page.waitForRequest(
          (request) =>
            new URL(request.url()).pathname === clonePath &&
            request.method() === "POST"
        ),
        submit.click(),
      ])
      expect(sent.postDataJSON()).toEqual({
        newName: "experiment",
        targetType: `${seed.team}/experiments`,
      })
      expect(download.suggestedFilename()).toBe("workflow-clone.zip")
      const destination = testInfo.outputPath("scaffold.zip")
      await download.saveAs(destination)
      expect(await download.failure()).toBeNull()
      const files = archiveFiles(destination)
      expect(Object.keys(files).sort()).toEqual(
        [
          "workflow.cs",
          "schedules.json",
          "schemas/schedules.schema.json",
          "WorkflowClone.Tests.csproj",
          "Tests/CloneTests.cs",
          "README.md",
        ].sort()
      )
      expect(files["workflow.cs"]).toContain(
        `"${seed.team}/experiments/experiment", "1.0.0"`
      )
      expect(files["workflow.cs"]).toContain(
        `${seed.workflow}@${seed.historical.id}`
      )
      expect(files["workflow.cs"]).toContain('.Type("experiments")')
      expect(files["workflow.cs"]).toContain("#:package Wf.Sdk@1.0.0")
      expect(files["workflow.cs"]).toContain(
        'ctx.Outputs["message"] = "historical-clone-body"'
      )
      expect(files["workflow.cs"]).not.toContain("latest-do-not-clone")
      expect(JSON.parse(files["schedules.json"]!).instances).toEqual([])
      expect(files["Tests/CloneTests.cs"]).toContain(seed.historical.id)
      await expect(dialog.getByRole("status")).toContainText(
        "Scaffold downloaded"
      )
      await accessible(page)
      await dialog.getByRole("button", { name: "Close", exact: true }).click()
      await expect(dialog).toBeHidden()
      await expect(trigger).toBeFocused()
      await expect(trigger).toBeEnabled()
      expect(await snapshot(request, seed.path, seed.historical.id)).toEqual(
        before
      )
    })
  }
}

test("actual target conflict preserves accessible draft and never starts a download", async ({
  page,
  request,
}) => {
  test.setTimeout(90_000)
  const seed = await fixture(request)
  await seed.publish(
    `${seed.team}/experiments/existing`,
    "1.0.0",
    "existing-target"
  )
  const before = await snapshot(request, seed.path, seed.historical.id)
  await page.goto(seed.route)
  const trigger = page.getByRole("button", {
    name: `Clone version ${seed.historical.id}`,
  })
  await trigger.click()
  const dialog = page.getByRole("alertdialog", {
    name: "Clone workflow version",
  })
  await dialog.getByLabel("New workflow name").fill("existing")
  await dialog.getByLabel("Target team/type").fill(`${seed.team}/experiments`)
  let downloaded = false
  page.on("download", () => {
    downloaded = true
  })
  const submit = dialog.getByRole("button", {
    name: "Download scaffold",
    exact: true,
  })
  await expect(submit).toBeEnabled()
  const response = page.waitForResponse((response) =>
    response.url().endsWith(`/versions/${seed.historical.id}/clone`)
  )
  await submit.click()
  expect((await response).status()).toBe(409)
  await expect(dialog.getByRole("alert")).toContainText(/exists|registered/i)
  await expect(dialog.getByLabel("New workflow name")).toHaveValue("existing")
  await expect(dialog.getByLabel("Target team/type")).toHaveValue(
    `${seed.team}/experiments`
  )
  await expect(submit).toBeEnabled()
  await accessible(page)
  await page.keyboard.press("Escape")
  await expect(dialog).toBeHidden()
  await expect(trigger).toBeFocused()
  expect(downloaded).toBe(false)
  expect(await snapshot(request, seed.path, seed.historical.id)).toEqual(before)
})
