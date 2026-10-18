import { execFileSync } from "node:child_process"
import { readFile } from "node:fs/promises"
import { resolve } from "node:path"
import AxeBuilder from "@axe-core/playwright"
import { expect, test } from "@playwright/test"
import { z } from "zod"

const root = resolve(import.meta.dirname, "..", "..")
const storage = resolve(root, ".agent-artifacts", "task-outputs-3adec91f")
const attemptSchema = z
  .object({
    Attempt: z.number(),
    QueuedAt: z.string(),
    DispatchedAt: z.string(),
    StartedAt: z.string().nullable(),
    EndedAt: z.string().nullable(),
    QueueWaitMs: z.number(),
    DurationMs: z.number(),
    TerminalStatus: z.number().nullable(),
  })
  .passthrough()
const entitySchema = z
  .object({
    QueuedAt: z.string().nullable(),
    StartedAt: z.string().nullable(),
    EndedAt: z.string().nullable(),
    QueueWaitMs: z.number(),
    DurationMs: z.number(),
    TerminalStatus: z.number().nullable(),
  })
  .passthrough()
const fixtureSchema = z.object({
  snapshot: z
    .object({
      RunId: z.uuid(),
      LastSequence: z.number(),
      Status: z.number(),
      EndedAt: z.string().nullable(),
      Nodes: z.record(z.string(), z.number()),
      Metrics: z
        .object({
          Run: entitySchema,
          Nodes: z.record(z.string(), entitySchema),
          Tasks: z.record(
            z.string(),
            z.object({ Attempts: z.array(attemptSchema) }).passthrough()
          ),
        })
        .passthrough(),
    })
    .passthrough(),
  outputs: z.array(z.record(z.string(), z.unknown())),
})
const literal = (value: string) => "'" + value.replaceAll("'", "''") + "'"
const ticks = (value: string) =>
  BigInt(Date.parse(value)) * 10000n +
  BigInt((/\.(\d+)/.exec(value)?.[1] ?? "").padEnd(7, "0").slice(3))

test("Outputs tab reads actual SDK attempt snapshots through real HTTP during/after run, keyboard/mobile/themes and safe JSON", async ({
  page,
  request,
}, info) => {
  test.setTimeout(90_000)
  const stack = z
    .object({
      container: z.literal("wf-task-outputs-3adec91f"),
      workflowId: z.string(),
    })
    .parse(
      JSON.parse(
        await readFile(resolve(storage, "browser", "ready.json"), "utf8")
      )
    )
  // These snapshots/outputs were exported by the actual process agent + SDK + gRPC + PostgreSQL integration test.
  const fixture = fixtureSchema.parse(
    JSON.parse(
      await readFile(resolve(storage, "browser-attempt-fixture.json"), "utf8")
    )
  )
  const sql = (statement: string) =>
    execFileSync(
      "docker",
      [
        "exec",
        stack.container,
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
      { encoding: "utf8", timeout: 10_000 }
    ).trim()
  const accepted = await request.post("/api/v1/runs", {
    data: { workflowId: stack.workflowId },
  })
  expect(accepted.status(), await accepted.text()).toBe(202)
  const runId = z.object({ runId: z.uuid() }).parse(await accepted.json()).runId
  await expect
    .poll(() => sql(`SELECT count(*) FROM runs WHERE id='${runId}'`))
    .toBe("1")
  const snapshot = { ...fixture.snapshot, RunId: runId }
  const seq = snapshot.LastSequence
  sql(`INSERT INTO run_snapshots(run_day,run_id,seq,created_at,payload)
    VALUES(uuid_v7_run_day('${runId}'),'${runId}',${seq},clock_timestamp(),convert_to(${literal(JSON.stringify(snapshot))},'UTF8'));
    UPDATE runs SET snapshot_seq=${seq},last_seq=${seq},status=1,started_at=${literal(snapshot.Metrics.Run.StartedAt!)} WHERE id='${runId}';`)
  const attempts = snapshot.Metrics.Tasks["work/failedOutputs"]!.Attempts
  expect(attempts).toHaveLength(2)
  for (const [index, row] of attempts.entries())
    sql(`INSERT INTO task_attempts(run_day,run_id,task_key,attempt,status,deadline_at,queued_at,started_at,ended_at,queue_wait_ms,outputs)
      VALUES(uuid_v7_run_day('${runId}'),'${runId}','work/failedOutputs',${row.Attempt},${row.TerminalStatus},
        clock_timestamp()+interval '1 hour',${literal(row.QueuedAt)},${literal(row.StartedAt!)},${literal(row.EndedAt!)},${row.QueueWaitMs},
        ${literal(JSON.stringify(fixture.outputs[index]))}::json);`)
  const outputUrl = (attempt: number) =>
    `/api/v1/runs/${runId}/outputs?taskKey=work%2FfailedOutputs&attempt=${attempt}`
  const during = await request.get(outputUrl(1))
  expect(during.status(), await during.text()).toBe(200)
  expect(during.headers()["cache-control"]).toBe("no-store")
  expect((await during.json()).outputs).toEqual(fixture.outputs[0])
  let reads = 0
  page.on("request", (req) => {
    if (new URL(req.url()).pathname === `/api/v1/runs/${runId}/outputs`) reads++
  })
  await page.goto(`/runs/${runId}`)
  const graph = page.getByRole("tab", { name: "Graph", exact: true })
  await expect(graph).toHaveAttribute("aria-selected", "true")
  expect(reads).toBe(0)
  await page.getByRole("tab", { name: "Metrics", exact: true }).focus()
  await page.keyboard.press("ArrowRight")
  const outputsTab = page.getByRole("tab", { name: "Outputs", exact: true })
  await expect(outputsTab).toBeFocused()
  await expect(outputsTab).toHaveAttribute("aria-selected", "true")
  const json = page.getByLabel("Attempt JSON outputs")
  await expect(json).toBeVisible()
  await expect(json).toContainText('"partial": 1')
  await expect(json).toContainText(
    "<script>window.outputExecuted=true</script>"
  )
  await expect(json).toContainText('"null": null')
  expect(
    await page.evaluate(() => Reflect.get(window, "outputExecuted"))
  ).toBeUndefined()
  expect(reads).toBe(1)
  const select = page.getByLabel("Output attempt")
  await select.focus()
  await page.keyboard.press("ArrowDown")
  // Native select commit differs by OS/browser; Enter commits the focused option.
  await page.keyboard.press("Enter")
  await expect(select).toHaveValue('["work/failedOutputs",2]')
  await expect(json).toContainText('"partial": 2')
  expect((await (await request.get(outputUrl(1))).json()).outputs).toEqual(
    fixture.outputs[0]
  )
  for (const width of [1440, 375])
    for (const dark of [false, true]) {
      await page.setViewportSize({ width, height: width === 375 ? 812 : 1000 })
      await page.evaluate(
        (enabled) => document.documentElement.classList.toggle("dark", enabled),
        dark
      )
      await expect(json).toBeVisible()
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
      await page.screenshot({
        path: resolve(
          storage,
          "browser",
          `${info.project.name}-${width}-${dark ? "dark" : "light"}.png`
        ),
        fullPage: true,
      })
    }
  const refresh = page.getByRole("button", { name: "Refresh task outputs" })
  sql(
    `UPDATE task_attempts SET outputs=NULL WHERE run_id='${runId}' AND attempt=2;`
  )
  await refresh.click()
  await expect(
    page.getByText("No structured outputs recorded for this attempt.", {
      exact: true,
    })
  ).toBeVisible()
  await expect(json).toHaveCount(0)
  sql(
    `UPDATE task_attempts SET outputs='{}'::json WHERE run_id='${runId}' AND attempt=2;`
  )
  await refresh.click()
  await expect(json).toHaveText("{}")
  sql(
    `UPDATE task_attempts SET outputs=${literal(JSON.stringify(fixture.outputs[1]))}::json WHERE run_id='${runId}' AND attempt=2;`
  )
  await refresh.click()
  await expect(json).toContainText('"partial": 2')
  const endedAt = new Date().toISOString()
  const terminal = (entity: z.infer<typeof entitySchema>, status: number) => ({
    ...entity,
    EndedAt: endedAt,
    TerminalStatus: status,
    FailureCategory: 0,
    DurationMs: entity.StartedAt
      ? Number(ticks(endedAt) - ticks(entity.StartedAt)) / 10000
      : 0,
  })
  const completed = {
    ...snapshot,
    Status: 4,
    EndedAt: endedAt,
    Nodes: { ...snapshot.Nodes, work: 7 },
    Metrics: {
      ...snapshot.Metrics,
      Run: terminal(snapshot.Metrics.Run, 4),
      Nodes: {
        ...snapshot.Metrics.Nodes,
        work: terminal(snapshot.Metrics.Nodes.work!, 7),
      },
    },
  }
  sql(`UPDATE run_snapshots SET payload=convert_to(${literal(JSON.stringify(completed))},'UTF8') WHERE run_id='${runId}' AND seq=${seq};
    UPDATE runs SET status=4,ended_at=${literal(endedAt)} WHERE id='${runId}';`)
  expect((await (await request.get(outputUrl(2))).json()).outputs).toEqual(
    fixture.outputs[1]
  )
  await page.reload()
  await outputsTab.click()
  await select.selectOption('["work/failedOutputs",2]')
  await expect(json).toContainText('"partial": 2')
  sql(`DELETE FROM task_attempts WHERE run_id='${runId}' AND attempt=2;`)
  await refresh.click()
  await expect(page.getByRole("alert")).toContainText("404")
  await expect(json).toHaveCount(0)
})
