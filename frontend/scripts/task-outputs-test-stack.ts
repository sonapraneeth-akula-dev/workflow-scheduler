import { spawn, spawnSync, type ChildProcess } from "node:child_process"
import { createHash, randomBytes, randomUUID } from "node:crypto"
import { watchFile, unwatchFile } from "node:fs"
import { mkdir, readFile, rm, writeFile } from "node:fs/promises"
import { resolve } from "node:path"
import { setTimeout as delay } from "node:timers/promises"

const root = resolve(import.meta.dirname, "..", "..")
const artifacts = resolve(root, ".agent-artifacts", "task-outputs-3adec91f")
const storage = resolve(artifacts, "browser")
const stopFile = resolve(storage, "stop")
const container = `kardia-test-task-outputs-postgres-${randomUUID().slice(0, 12)}`
const api = "http://127.0.0.1:4384"
const origin = "http://127.0.0.1:4383"
const host = resolve(
  artifacts,
  "bin",
  "Wf.ControlPlane",
  "debug",
  "Wf.ControlPlane.dll"
)
const children: ChildProcess[] = []
const shutdown = new AbortController()
let created = false
let log = ""
await mkdir(storage, { recursive: true })
await rm(stopFile, { force: true })
await rm(resolve(storage, "ready.json"), { force: true })
watchFile(stopFile, { interval: 200 }, (current) => {
  if (current.nlink > 0) shutdown.abort()
})
for (const signal of ["SIGINT", "SIGTERM"] as const)
  process.on(signal, () => shutdown.abort())
function run(command: string, args: string[], env = process.env) {
  const result = spawnSync(command, args, {
    cwd: root,
    env,
    encoding: "utf8",
    timeout: 120_000,
  })
  if (result.error) throw result.error
  if (result.status !== 0)
    throw new Error(`${command} failed: ${result.stdout}\n${result.stderr}`)
  return result.stdout.trim()
}
function start(command: string, args: string[], env: NodeJS.ProcessEnv) {
  const child = spawn(command, args, {
    cwd: resolve(root, "frontend"),
    env,
    stdio: ["ignore", "pipe", "pipe"],
  })
  children.push(child)
  child.on("error", (error) => {
    log += error.message
    shutdown.abort()
  })
  for (const stream of [child.stdout, child.stderr])
    stream?.on("data", (data: Buffer) => {
      log = (log + data.toString()).slice(-16_000)
    })
}
async function ready(url: string) {
  for (let attempt = 0; attempt < 600; attempt++) {
    if (
      shutdown.signal.aborted ||
      children.some((child) => child.exitCode !== null)
    )
      throw new Error(`Task outputs stack terminated.\n${log}`)
    try {
      if ((await fetch(url, { signal: AbortSignal.timeout(1_000) })).ok) return
    } catch {
      /* Wait for this owned service. */
    }
    await delay(100)
  }
  throw new Error(`Task outputs stack not ready at ${url}.\n${log}`)
}
async function post(path: string, body: unknown) {
  const response = await fetch(api + path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  })
  if (!response.ok)
    throw new Error(`${path}: ${response.status} ${await response.text()}`)
  return response.json() as Promise<Record<string, unknown>>
}
try {
  const password = randomBytes(24).toString("hex")
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
    "postgres:18.6-alpine",
  ])
  created = true
  const port = run("docker", ["port", container, "5432/tcp"]).split(":").at(-1)
  if (!port || !/^\d+$/.test(port)) throw new Error("Missing PostgreSQL port.")
  let pgReady = false
  for (let attempt = 0; attempt < 200; attempt++) {
    if (
      spawnSync(
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
        { stdio: "ignore" }
      ).status === 0
    ) {
      pgReady = true
      break
    }
    await delay(100)
  }
  if (!pgReady) throw new Error("PostgreSQL not ready.")
  const environment = {
    ...process.env,
    DOTNET_ENVIRONMENT: "Development",
    ASPNETCORE_ENVIRONMENT: "Development",
    ConnectionStrings__Workflow: `Host=127.0.0.1;Port=${port};Database=workflow;Username=workflow;Password=${password}`,
    Engine__Port: "4385",
    Api__Port: "4384",
    Storage__Root: resolve(storage, "blobs"),
    Storage__UploadHmacKey: randomBytes(32).toString("base64"),
    VITE_AUTH_MODE: "development",
    KARDIA_API_URL: api,
    TEMP: root,
    TMP: root,
    TMPDIR: root,
  }
  run("dotnet", [host, "--migrate"], environment)
  start("dotnet", [host, "--dev"], environment)
  await ready(`${api}/healthz/leader`)
  await readFile(resolve(artifacts, "browser-attempt-fixture.json"), "utf8")
  const bytes = Buffer.from(
    "Task outputs browser registration fixture; execution evidence exported by the real SDK integration test."
  )
  const sha256 = createHash("sha256").update(bytes).digest("hex")
  const grant = await post("/api/v1/upload-grants", {
    kind: "package",
    sha256,
    size: bytes.length,
  })
  if (grant.exists !== true) {
    if (typeof grant.uploadUri !== "string")
      throw new Error("Missing package grant.")
    const uploaded = await fetch(new URL(grant.uploadUri, api), {
      method: "PUT",
      body: bytes,
    })
    if (!uploaded.ok) throw new Error(`Package upload: ${uploaded.status}`)
  }
  const workflowId = "output-tests/release/workflow"
  await post("/api/v1/registrations", {
    gitCommit: "a".repeat(40),
    gitAuthor: "Task outputs browser integration",
    workflowId,
    package: { sha256, size: bytes.length },
    source: "// Task outputs browser registration fixture",
    registry: {
      team: "output-tests",
      types: [
        {
          team: "output-tests",
          name: "release",
          description: "Task outputs",
          metadataSchema: { keys: [], allowFreeForm: true },
        },
      ],
    },
    manifest: {
      sdkVersion: "1.0.0",
      name: workflowId,
      version: "1.0.0",
      metadata: { type: "release", values: {} },
      variables: [
        {
          key: "amount",
          type: "Int",
          required: false,
          default: 42,
          allowedValues: [],
        },
      ],
      stages: [],
      runTimeout: "00:02:00",
      nodes: [
        {
          key: "work",
          dependencies: [],
          mode: "Series",
          failure: "Hard",
          inputs: [],
          timeout: null,
          maxParallel: null,
          tasks: ["failedOutputs"].map((key) => ({
            key,
            failure: key === "failedOutputs" ? "Soft" : "Hard",
            policy: {
              attemptTimeout: "00:01:00",
              autoRetry: true,
              retry: {
                retries: key === "failedOutputs" ? 1 : 0,
                on: ["TaskError", "Timeout"],
                backoff: {
                  kind: "Fixed",
                  delay: "00:00:01",
                  maxDelay: "00:00:01",
                },
              },
            },
            action: {
              kind: "SdkFunction",
              declaration: `OutputsFixture.${key}`,
            },
          })),
        },
      ],
    },
  })
  start(
    process.execPath,
    [
      resolve(root, "frontend", "node_modules", "vite", "bin", "vite.js"),
      "--host",
      "127.0.0.1",
      "--port",
      "4383",
      "--strictPort",
    ],
    environment
  )
  await ready(origin)
  await writeFile(
    resolve(storage, "ready.json"),
    JSON.stringify({
      origin,
      api,
      container,
      workflowId,
      pids: children.map((child) => child.pid),
    })
  )
  console.log(
    `Task outputs stack ready: ${origin}; API ${api}; stop: ${stopFile}`
  )
  while (!shutdown.signal.aborted) {
    if (children.some((child) => child.exitCode !== null))
      throw new Error(`Task outputs stack exited.\n${log}`)
    await delay(200)
  }
} finally {
  unwatchFile(stopFile)
  await writeFile(resolve(storage, "service.log"), log)
  for (const child of children.reverse())
    if (child.exitCode === null) {
      child.kill("SIGTERM")
      await Promise.race([
        new Promise<void>((done) => child.once("exit", () => done())),
        delay(5_000),
      ])
      if (child.exitCode === null) child.kill("SIGKILL")
    }
  if (created) run("docker", ["rm", "--force", container])
  await rm(resolve(storage, "ready.json"), { force: true })
}
