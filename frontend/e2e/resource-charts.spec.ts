import { execFileSync } from "node:child_process"
import { createHash } from "node:crypto"
import { mkdir, readFile, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { resolve } from "node:path"
import AxeBuilder from "@axe-core/playwright"
import { expect, test } from "@playwright/test"
import { z } from "zod"
import {
  getResourcePage,
  getResourceSummary,
} from "../src/features/runs/resource-api"

interface Pointer {
  readonly key: string
  readonly sha256: string
  readonly sizeBytes: number
}
async function seed(
  storage: string,
  runId: string,
  taskKey: string,
  count: number,
  kind: "windows" | "linux",
  sql: (statement: string) => string
) {
  const taskHash = createHash("sha256").update(taskKey).digest("hex")
  const directory = resolve(storage, "resources", runId, taskHash, "1")
  await mkdir(directory, { recursive: true })
  let previous: Pointer | null = null
  let latest: Buffer = Buffer.alloc(0)
  for (let sequence = 1; sequence <= count; sequence++) {
    const baseline = sequence === 1
    const windows = kind === "windows"
    const counter = 9007199254740993n + BigInt(Math.max(0, sequence - 2)) * 10n
    const delta = sequence === 2 ? 0 : 10
    const atUtc = new Date(
      Date.UTC(2026, 9, 16, 12, 0, sequence < 33 ? sequence : sequence - 20)
    ).toISOString()
    const task = {
      atUtc,
      intervalSeconds: baseline ? null : 1,
      cpuPercent: baseline ? null : sequence === 2 ? 0 : 125,
      ramBytes: 33554432 + sequence * 1024,
      networkSentBytes: windows ? counter : null,
      networkReceivedBytes: windows ? counter : null,
      networkSentDeltaBytes: windows && !baseline ? delta : null,
      networkReceivedDeltaBytes: windows && !baseline ? delta : null,
      networkSentBytesPerSecond: windows && !baseline ? delta : null,
      networkReceivedBytesPerSecond: windows && !baseline ? delta : null,
      cpuScope: windows ? "job-with-descendants" : "cgroup-with-descendants",
      memoryScope: windows ? "job-resident-sum" : "cgroup-memory.current",
      networkScope: windows ? "host-process-etw-transport" : "unavailable",
      cpuUnavailableReason: baseline ? "First CPU baseline." : null,
      memoryUnavailableReason: null,
      networkUnavailableReason: windows
        ? null
        : "CAP_BPF is unavailable for this fixture.",
      networkRateUnavailableReason: windows
        ? baseline
          ? "First network baseline."
          : null
        : "No task network counters available.",
    }
    const machine = {
      ...task,
      cpuPercent: baseline ? null : 17,
      ramBytes: 8589934592n,
      cpuScope: "machine",
      memoryScope: "physical-used",
      networkScope: "non-loopback-interfaces",
      networkSentBytes: 4000 + sequence * 40,
      networkReceivedBytes: 8000 + sequence * 80,
      networkSentDeltaBytes: baseline ? null : 40,
      networkReceivedDeltaBytes: baseline ? null : 80,
      networkSentBytesPerSecond: baseline ? null : 40,
      networkReceivedBytesPerSecond: baseline ? null : 80,
      networkUnavailableReason: null,
      networkRateUnavailableReason: baseline ? "First machine baseline." : null,
    }
    const bytes: Buffer = Buffer.from(
      JSON.stringify(
        {
          sequence,
          observation: { task, machine, machineUnavailableReason: null },
          previous,
          schemaVersion: 1,
        },
        (_, value: unknown) =>
          typeof value === "bigint" ? JSON.rawJSON(String(value)) : value
      )
    )
    const sha256 = createHash("sha256").update(bytes).digest("hex")
    const key = `resources/${runId}/${taskHash}/1/${sequence}-${sha256}.json`
    previous = { key, sha256, sizeBytes: bytes.length }
    await writeFile(resolve(directory, `${sequence}-${sha256}.json`), bytes, {
      flag: "wx",
    })
    latest = bytes
  }
  if (!previous) throw new Error("Resource fixture requires samples.")
  const cpuSeconds = count - 1
  const cpuTotal = Math.max(0, count - 2) * 125
  sql(`INSERT INTO task_attempts(run_day,run_id,task_key,attempt,status,deadline_at,queued_at,started_at,ended_at,
    resource_sample_count,resource_series_uri,resource_series_sha256,resource_series_bytes,
    resource_cpu_percent_seconds,resource_cpu_observed_seconds,resource_avg_cpu_percent,resource_max_cpu_percent,
    resource_peak_ram_bytes,resource_network_sent_bytes,resource_network_received_bytes,resource_network_observed_seconds)
    VALUES(uuid_v7_run_day('${runId}'),'${runId}','${taskKey}',1,5,clock_timestamp()+interval '1 hour',
      clock_timestamp(),clock_timestamp(),clock_timestamp(),${count},'${previous.key}','${previous.sha256}',${previous.sizeBytes},
      ${cpuTotal},${cpuSeconds},${cpuTotal / cpuSeconds},125,${33554432 + count * 1024},
      ${kind === "windows" ? Math.max(0, count - 2) * 10 : "NULL"},
      ${kind === "windows" ? Math.max(0, count - 2) * 10 : "NULL"},${kind === "windows" ? cpuSeconds : 0});`)
  return { path: resolve(storage, ...previous.key.split("/")), bytes: latest }
}

test("Metrics tab reads retained integrity-checked resource pages, exact counters, OS scopes and missing measurements", async ({
  page,
  request,
}) => {
  test.setTimeout(90_000)
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
  const accepted = await request.post("/api/v1/runs", {
    data: { workflowId: "ui-tests/release/workflow" },
  })
  expect(accepted.status()).toBe(202)
  const runId = z.object({ runId: z.uuid() }).parse(await accepted.json()).runId
  const sql = (statement: string) =>
    execFileSync(
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
        "-v",
        "ON_ERROR_STOP=1",
        "-c",
        statement,
      ],
      { encoding: "utf8", timeout: 20_000 }
    ).trim()
  await expect
    .poll(() => sql(`SELECT count(*) FROM runs WHERE id='${runId}'`))
    .toBe("1")
  const storage = resolve(tmpdir(), fixture.directory)
  // Retained contract fixtures exercise real HTTP/blob integrity, not a new sampler or dispatch claim.
  const windows = await seed(storage, runId, "work/windows", 65, "windows", sql)
  await seed(storage, runId, "work/linux", 3, "linux", sql)
  const reads: string[] = []
  page.on("request", (request) => {
    const path = new URL(request.url()).pathname
    if (path.endsWith("/resources")) reads.push(request.url())
  })
  await page.goto(`/runs/${runId}`)
  await page.getByRole("tab", { name: "Metrics", exact: true }).click()
  const summary = page.getByRole("region", {
    name: "Attempt resource summaries",
  })
  await expect(summary).toContainText("work/windows")
  await expect(summary).toContainText("work/linux")
  const attempt = page.getByLabel("Resource attempt", { exact: true })
  await attempt.selectOption({ label: "work/windows - attempt 1" })
  const exact = page.getByRole("region", { name: "Exact resource samples" })
  await expect(exact.getByRole("row")).toHaveCount(61)
  await expect(exact).toContainText("9,007,199,254,741,623 B")
  await expect(exact).toContainText("job-with-descendants")
  await expect(exact).toContainText("host-process-etw-transport")
  for (const title of ["Task CPU (%)", "Task RAM (MiB)", "Task network (B/s)"])
    await expect(
      page
        .getByRole("img", { name: `${title}: line chart`, exact: true })
        .locator("canvas")
    ).toBeVisible()
  expect(
    await page
      .getByRole("tabpanel")
      .filter({ has: page.getByRole("heading", { name: "Resource metrics" }) })
      .locator("canvas")
      .evaluateAll((canvases) =>
        canvases.every((element) => {
          if (!(element instanceof HTMLCanvasElement)) return false
          const canvas = element
          const context = canvas.getContext("2d")
          if (!context || canvas.width < 100 || canvas.height < 100)
            return false
          const pixels = context.getImageData(
            0,
            0,
            canvas.width,
            canvas.height
          ).data
          for (let index = 0; index < pixels.length; index += 4)
            if (
              pixels[index + 3]! > 0 &&
              ((Math.abs(pixels[index]! - 37) < 30 &&
                Math.abs(pixels[index + 1]! - 99) < 30 &&
                Math.abs(pixels[index + 2]! - 235) < 30) ||
                (Math.abs(pixels[index]! - 220) < 30 &&
                  Math.abs(pixels[index + 1]! - 38) < 30 &&
                  Math.abs(pixels[index + 2]! - 38) < 30))
            )
              return true
          return false
        })
      )
  ).toBe(true)
  await page.getByRole("button", { name: "Older samples", exact: true }).click()
  await expect(exact.getByRole("row")).toHaveCount(6)
  await expect(exact).toContainText("First CPU baseline.")
  await expect(exact).toContainText("First network baseline.")
  await expect(exact).toContainText("0%")
  await expect(
    page.getByRole("button", { name: "Older samples", exact: true })
  ).toBeDisabled()
  expect(reads.some((url) => new URL(url).searchParams.has("cursor"))).toBe(
    true
  )
  await page.getByRole("button", { name: "Newer samples", exact: true }).click()
  await expect(exact.getByRole("row")).toHaveCount(61)
  await page
    .getByLabel("Resource scope", { exact: true })
    .selectOption("machine")
  await expect(
    page.getByRole("img", { name: "Machine CPU (%): line chart", exact: true })
  ).toBeVisible()
  await expect(exact).toContainText("8,589,934,592 B")
  await expect(exact).toContainText("non-loopback-interfaces")
  await attempt.selectOption({ label: "work/linux - attempt 1" })
  await page.getByLabel("Resource scope", { exact: true }).selectOption("task")
  await expect(exact.getByRole("row")).toHaveCount(4)
  await expect(exact).toContainText("cgroup-with-descendants")
  await expect(exact).toContainText("CAP_BPF is unavailable for this fixture.")
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
  }
  await attempt.selectOption({ label: "work/windows - attempt 1" })
  await expect(exact.getByRole("row")).toHaveCount(61)
  await writeFile(
    windows.path,
    Buffer.from("corrupt fixture-owned resource bytes")
  )
  try {
    const failed = page.waitForResponse(
      (response) =>
        new URL(response.url()).pathname.endsWith("/resources") &&
        response.status() === 503
    )
    await page
      .getByRole("button", { name: "Refresh resource metrics", exact: true })
      .click()
    await failed
    await expect(page.getByRole("alert")).toContainText(
      "Resource metrics storage unavailable"
    )
    await expect(exact).toHaveCount(0)
    await expect(
      page.getByRole("img", { name: "Task CPU (%): line chart", exact: true })
    ).toHaveCount(0)
  } finally {
    await writeFile(windows.path, windows.bytes)
  }
  await page
    .getByRole("button", { name: "Refresh resource metrics", exact: true })
    .click()
  await expect(exact.getByRole("row")).toHaveCount(61)
  const result = await page.evaluate(async (runId) => {
    const path = "/src/features/runs/resource-api.ts"
    const api: {
      getResourceSummary: typeof getResourceSummary
      getResourcePage: typeof getResourcePage
    } = await import(path)
    const summaries = await api.getResourceSummary(runId)
    const windows = summaries.find((item) => item.taskKey === "work/windows")
    if (!windows) throw new Error("Windows resource fixture is missing.")
    return {
      summary: windows,
      page: await api.getResourcePage(runId, windows, null),
    }
  }, runId)
  expect(result.page.samples).toHaveLength(60)
  expect(result.page.samples[0]!.observation.task.networkSentBytes).toBe(
    "9007199254741623"
  )
  expect(result.summary.networkSentBytes).toBe("630")
})
