import { spawn, spawnSync, type ChildProcess } from "node:child_process"
import { randomBytes, randomUUID, createHash } from "node:crypto"
import { watchFile, unwatchFile } from "node:fs"
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { basename, resolve } from "node:path"
import { setTimeout as delay } from "node:timers/promises"
import { z } from "zod"
import { seedRendererReports } from "./seed-renderer-reports"
import "../src/lib/json"

const root = resolve(import.meta.dirname, "..", "..")
const api = "http://127.0.0.1:4174"
const host =
  process.env.KARDIA_HOST_DLL ??
  resolve(
    root,
    "backend",
    "src",
    "Wf.ControlPlane",
    "bin",
    "Release",
    "net10.0",
    "Wf.ControlPlane.dll"
  )
const container = `wf-ui-${randomUUID()}`
const password = randomBytes(24).toString("hex")
const storage = await mkdtemp(resolve(tmpdir(), "wf-ui-"))
const stopFile = resolve(storage, "stop")
const stateFile = new URL("../.test-stack.json", import.meta.url)
const children: ChildProcess[] = []
let containerCreated = false
let stateOwned = false
let output = ""
const shutdown = new AbortController()
watchFile(stopFile, { interval: 100 }, (current) => {
  if (current.nlink > 0) {
    shutdown.abort()
  }
})
for (const signal of ["SIGINT", "SIGTERM"] as const) {
  process.on(signal, () => shutdown.abort())
}

function run(
  command: string,
  arguments_: string[],
  environment = process.env
): string {
  const result = spawnSync(command, arguments_, {
    cwd: root,
    env: environment,
    encoding: "utf8",
    timeout: 120_000,
  })
  if (result.error) {
    throw result.error
  }
  if (result.status !== 0) {
    throw new Error(
      `${command} failed (${result.status}): ${result.stdout}\n${result.stderr}`
    )
  }
  return result.stdout.trim()
}

function start(
  command: string,
  arguments_: string[],
  environment: NodeJS.ProcessEnv
): ChildProcess {
  const child = spawn(command, arguments_, {
    cwd: resolve(root, "frontend"),
    env: environment,
    stdio: ["ignore", "pipe", "pipe"],
  })
  children.push(child)
  child.on("error", (error) => {
    output += `\n${error.message}`
    shutdown.abort()
  })
  for (const stream of [child.stdout, child.stderr]) {
    stream?.on("data", (data: Buffer) => {
      output = (output + data.toString()).slice(-16_000)
    })
  }
  return child
}

async function ready(url: string): Promise<void> {
  const deadline = Date.now() + 60_000
  while (Date.now() < deadline) {
    if (
      shutdown.signal.aborted ||
      children.some((child) => child.exitCode !== null)
    ) {
      throw new Error(`UI stack terminated before readiness.\n${output}`)
    }
    try {
      const response = await fetch(url, { signal: AbortSignal.timeout(2_000) })
      if (response.ok) {
        return
      }
    } catch (error) {
      if (!(error instanceof TypeError) && !(error instanceof DOMException)) {
        throw error
      }
    }
    await delay(100)
  }
  throw new Error(`UI stack did not become ready at ${url}.\n${output}`)
}

async function request(
  path: string,
  body: unknown,
  method = "POST"
): Promise<unknown> {
  const response = await fetch(api + path, {
    method,
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  })
  if (!response.ok) {
    throw new Error(
      `Fixture ${path} failed: HTTP ${response.status}: ${await response.text()}`
    )
  }
  return response.json()
}

try {
  run("docker", [
    "run",
    "--detach",
    "--rm",
    "--name",
    container,
    "--env",
    "POSTGRES_USER=workflow",
    "--env",
    `POSTGRES_PASSWORD=${password}`,
    "--env",
    "POSTGRES_DB=workflow",
    "--publish",
    "127.0.0.1::5432",
    "postgres:17-alpine",
  ])
  containerCreated = true
  const port = run("docker", ["port", container, "5432/tcp"]).split(":").at(-1)
  if (!port || !/^\d+$/.test(port)) {
    throw new Error("Cannot determine UI PostgreSQL port.")
  }
  let postgresReady = false
  for (let attempt = 0; attempt < 100; attempt++) {
    const probe = spawnSync(
      "docker",
      [
        "exec",
        container,
        "pg_isready",
        "-h",
        "127.0.0.1",
        "-U",
        "workflow",
        "-d",
        "workflow",
      ],
      { encoding: "utf8" }
    )
    if (probe.status === 0) {
      postgresReady = true
      break
    }
    await delay(100)
  }
  if (!postgresReady) {
    throw new Error("UI PostgreSQL did not become ready.")
  }
  const environment = {
    ...process.env,
    DOTNET_ENVIRONMENT: "Development",
    ASPNETCORE_ENVIRONMENT: "Development",
    ConnectionStrings__Workflow: `Host=127.0.0.1;Port=${port};Database=workflow;Username=workflow;Password=${password}`,
    Engine__Port: "4175",
    Api__Port: "4174",
    Storage__Root: storage,
    Storage__UploadHmacKey: randomBytes(32).toString("base64"),
    VITE_AUTH_MODE: "development",
    VITE_COMMIT_URL_TEMPLATE:
      "https://github.com/example/workflows/commit/{commit}",
    KARDIA_API_URL: api,
  }
  run("dotnet", [host, "--migrate"], environment)
  start("dotnet", [host, "--dev"], environment)
  await ready(api + "/healthz/leader")
  run(
    process.execPath,
    [
      resolve(root, "frontend", "scripts", "generate-api.ts"),
      ...(process.argv.includes("--generate-api") ? [] : ["--check"]),
    ],
    environment
  )
  const manifest: unknown = JSON.parse(
    await readFile(
      resolve(
        root,
        "backend",
        "tests",
        "Wf.Sdk.Tests",
        "Snapshots",
        "workflow.manifest.json"
      ),
      "utf8"
    )
  )
  if (typeof manifest !== "object" || manifest === null) {
    throw new Error("Invalid committed fixture manifest.")
  }
  const bytes = new TextEncoder().encode("UI schedule test package")
  const sha256 = createHash("sha256").update(bytes).digest("hex")
  const grant = await request("/api/v1/upload-grants", {
    kind: "package",
    sha256,
    size: bytes.length,
  })
  if (
    typeof grant !== "object" ||
    grant === null ||
    !("uploadUri" in grant) ||
    typeof grant.uploadUri !== "string"
  ) {
    throw new Error("The test package upload grant is missing.")
  }
  const uploaded = await fetch(new URL(grant.uploadUri, api), {
    method: "PUT",
    body: bytes,
  })
  if (!uploaded.ok) {
    throw new Error(`UI fixture package upload failed: HTTP ${uploaded.status}`)
  }
  await request("/api/v1/registrations", {
    gitCommit: "a".repeat(40),
    gitAuthor: "UI integration",
    workflowId: "ui-tests/release/workflow",
    manifest: {
      ...manifest,
      name: "ui-tests/release/workflow",
      metadata: { type: "release", values: {} },
      variables: [
        {
          key: "sequence",
          type: "Long",
          required: false,
          default: JSON.rawJSON("9223372036854775807"),
          allowedValues: [],
        },
      ],
    },
    package: { sha256, size: bytes.length },
    source: "// schedule UI integration",
    schedules:
      '{"instances":[{"id":"code-nightly","timezone":"Etc/UTC","interval":"01:00:00"}]}',
    scheduleSourcePath: "workflows/ui-tests.schedules.json",
    registry: {
      team: "ui-tests",
      types: [
        {
          team: "ui-tests",
          name: "release",
          description: "UI integration",
          metadataSchema: { keys: [] },
        },
      ],
    },
  })
  for (const browser of ["chromium", "firefox", "webkit"]) {
    const workflowId = `ui-tests/release/catalog-${browser}`
    await request("/api/v1/registrations", {
      gitCommit: "b".repeat(40),
      gitAuthor: "Catalogue UI integration",
      workflowId,
      manifest: {
        ...manifest,
        name: workflowId,
        metadata: { type: "release", values: {} },
      },
      package: { sha256, size: bytes.length },
      source: "// catalogue UI integration",
      registry: {
        team: "ui-tests",
        types: [
          {
            team: "ui-tests",
            name: "release",
            description: "UI integration",
            metadataSchema: { keys: [] },
          },
        ],
      },
    })
  }
  for (const team of ["presentation-a", "presentation-b"]) {
    const presentation =
      team === "presentation-a"
        ? {
            typeOrder: ["perf", "release"],
            groupingKeys: ["environment"],
            listColumns: ["component"],
            defaultFilters: { environment: "dev" },
            labels: {
              environment: "Target environment",
              component: "Component name",
            },
          }
        : undefined
    const types = ["release", "perf"].map((name) => ({
      team,
      name,
      description: `${name} presentation fixture`,
      metadataSchema: {
        keys: [
          { key: "environment", allowedValues: ["dev", "prod"] },
          { key: "component" },
        ],
      },
    }))
    for (const environment of ["dev", "prod"]) {
      const workflowId = `${team}/release/${environment}-workflow`
      await request("/api/v1/registrations", {
        gitCommit: "c".repeat(40),
        gitAuthor: "Presentation UI integration",
        workflowId,
        manifest: {
          ...manifest,
          name: workflowId,
          metadata: {
            type: "release",
            values: {
              environment,
              component: environment === "dev" ? "api" : "web",
            },
          },
        },
        package: { sha256, size: bytes.length },
        source: "// presentation UI integration",
        registry: { team, types, ...(presentation ? { presentation } : {}) },
      })
    }
  }
  for (const browser of ["chromium", "firefox", "webkit"]) {
    for (const environment of ["dev", "prod"]) {
      const workflowId = `filter-tests/search/${browser}-${environment}`
      await request("/api/v1/registrations", {
        gitCommit: "d".repeat(40),
        gitAuthor: "Run filter UI integration",
        workflowId,
        manifest: {
          ...manifest,
          name: workflowId,
          metadata: {
            type: "search",
            values: {
              environment,
              component: environment === "dev" ? "api" : "web",
            },
          },
        },
        package: { sha256, size: bytes.length },
        source: "// run filter UI integration",
        registry: {
          team: "filter-tests",
          types: [
            {
              team: "filter-tests",
              name: "search",
              description: "Run filter UI integration",
              metadataSchema: {
                keys: [{ key: "environment" }, { key: "component" }],
              },
            },
          ],
        },
      })
    }
  }
  const codeScheduleId = z
    .uuid()
    .parse(
      run("docker", [
        "exec",
        container,
        "psql",
        "-U",
        "workflow",
        "-d",
        "workflow",
        "-Atc",
        "SELECT id FROM schedules WHERE workflow_id='ui-tests/release/workflow' AND origin=0",
      ])
    )
  const history = z.object({ id: z.uuid(), seriesId: z.uuid() }).parse(
    await request("/api/v1/schedules", {
      workflowId: "ui-tests/release/workflow",
      name: "Retained history fixture",
      timezone: "Etc/UTC",
      interval: "01:00:00",
    })
  )
  run("docker", [
    "exec",
    container,
    "psql",
    "-U",
    "workflow",
    "-d",
    "workflow",
    "-v",
    "ON_ERROR_STOP=1",
    "-c",
    `INSERT INTO schedule_fires(schedule_id,fire_at,outcome,reason)
     SELECT '${history.id}'::uuid,'2026-01-01'::timestamptz+n*interval '1 minute',1,'overlap'
     FROM generate_series(0,59) n;`,
  ])
  const catchUp = z.object({ id: z.uuid(), seriesId: z.uuid() }).parse(
    await request("/api/v1/schedules", {
      workflowId: "ui-tests/release/workflow",
      name: "Actual catch-up fixture",
      timezone: "Etc/UTC",
      interval: "00:01:00",
      catchUp: "Latest",
    })
  )
  run("docker", [
    "exec",
    container,
    "psql",
    "-U",
    "workflow",
    "-d",
    "workflow",
    "-v",
    "ON_ERROR_STOP=1",
    "-c",
    `BEGIN;
     UPDATE schedules SET next_fire_at=date_trunc('second',clock_timestamp())-interval '9 minutes 30 seconds'
     WHERE id='${catchUp.id}'::uuid;
     SELECT pg_notify('schedules','${catchUp.id}');
     COMMIT;`,
  ])
  const rendererWorkflow = "report-tests/viewer/basic"
  await request("/api/v1/registrations", {
    gitCommit: "e".repeat(40),
    gitAuthor: "Report renderer integration",
    workflowId: rendererWorkflow,
    manifest: {
      ...manifest,
      name: rendererWorkflow,
      metadata: { type: "viewer", values: {} },
    },
    package: { sha256, size: bytes.length },
    source: "// report renderer integration",
    registry: {
      team: "report-tests",
      types: [
        {
          team: "report-tests",
          name: "viewer",
          description: "Report renderer integration",
          metadataSchema: { keys: [] },
        },
      ],
    },
  })
  const rendererRunId = z
    .object({ runId: z.uuid() })
    .parse(
      await request("/api/v1/runs", {
        workflowId: rendererWorkflow,
        variables: {},
      })
    ).runId
  const reportSql = (statement: string) =>
    run("docker", [
      "exec",
      container,
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
    ])
  const reportDeadline = Date.now() + 20_000
  while (
    reportSql(`SELECT count(*) FROM runs WHERE id='${rendererRunId}'`) !== "1"
  ) {
    if (Date.now() >= reportDeadline)
      throw new Error("Report fixture run was not committed.")
    await delay(50)
  }
  const rendererReports = await seedRendererReports(
    storage,
    rendererRunId,
    reportSql
  )
  start(
    process.execPath,
    [
      resolve(root, "frontend", "node_modules", "vite", "bin", "vite.js"),
      "--host",
      "127.0.0.1",
      "--port",
      "4173",
      "--strictPort",
    ],
    environment
  )
  await ready("http://127.0.0.1:4173")
  await writeFile(
    stateFile,
    JSON.stringify({
      directory: basename(storage),
      historyScheduleId: history.id,
      historySeriesId: history.seriesId,
      catchUpSeriesId: catchUp.seriesId,
      codeScheduleId,
      rendererRunId,
      rendererReports,
    }),
    {
      flag: "wx",
    }
  )
  stateOwned = true
  console.log(
    `Schedule UI test stack ready: http://127.0.0.1:4173 (${container})`
  )
  await new Promise<void>((resolveShutdown) => {
    if (shutdown.signal.aborted) {
      resolveShutdown()
    } else {
      shutdown.signal.addEventListener("abort", () => resolveShutdown(), {
        once: true,
      })
    }
  })
} finally {
  unwatchFile(stopFile)
  for (const child of children.reverse()) {
    if (child.exitCode === null && child.pid) {
      child.kill()
      await new Promise<void>((resolveExit) =>
        child.once("exit", () => resolveExit())
      )
    }
  }
  try {
    if (containerCreated) {
      run("docker", ["rm", "--force", container])
    }
  } finally {
    await rm(storage, { recursive: true, force: true })
    if (stateOwned) {
      await rm(stateFile, { force: true })
    }
  }
}
