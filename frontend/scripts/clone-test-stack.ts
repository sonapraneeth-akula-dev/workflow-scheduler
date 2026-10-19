import { spawn, spawnSync, type ChildProcess } from "node:child_process"
import { randomBytes, randomUUID } from "node:crypto"
import { watchFile, unwatchFile } from "node:fs"
import { mkdir, rm, writeFile } from "node:fs/promises"
import { resolve } from "node:path"
import { setTimeout as delay } from "node:timers/promises"

const root = resolve(import.meta.dirname, "..", "..")
const storage = resolve(
  root,
  ".agent-artifacts",
  "clone-clients-3adec91f",
  "browser"
)
const stopFile = resolve(storage, "stop")
const container = `kardia-test-clone-postgres-${randomUUID().slice(0, 12)}`
const api = "http://127.0.0.1:4284"
const origin = "http://127.0.0.1:4283"
const host = resolve(
  root,
  ".agent-artifacts",
  "clone-clients-3adec91f",
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
  if (current.nlink > 0) {
    shutdown.abort()
  }
})
for (const signal of ["SIGINT", "SIGTERM"] as const) {
  process.on(signal, () => shutdown.abort())
}
function run(command: string, args: string[], env = process.env) {
  const result = spawnSync(command, args, {
    cwd: root,
    env,
    encoding: "utf8",
    timeout: 120_000,
  })
  if (result.error) {
    throw result.error
  }
  if (result.status !== 0) {
    throw new Error(`${command} failed: ${result.stderr}`)
  }
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
  for (const stream of [child.stdout, child.stderr]) {
    stream?.on("data", (data: Buffer) => {
      log = (log + data.toString()).slice(-16_000)
    })
  }
}
async function ready(url: string) {
  for (let attempt = 0; attempt < 600; attempt++) {
    if (
      shutdown.signal.aborted ||
      children.some((child) => child.exitCode !== null)
    ) {
      throw new Error(`Clone stack terminated.\n${log}`)
    }
    try {
      if ((await fetch(url, { signal: AbortSignal.timeout(1_000) })).ok) {
        return
      }
    } catch {
      /* Wait for the owned service. */
    }
    await delay(100)
  }
  throw new Error(`Clone stack not ready at ${url}.\n${log}`)
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
  if (!port || !/^\d+$/.test(port)) {
    throw new Error("Missing PostgreSQL port.")
  }
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
  if (!pgReady) {
    throw new Error("PostgreSQL not ready.")
  }
  const environment = {
    ...process.env,
    DOTNET_ENVIRONMENT: "Development",
    ASPNETCORE_ENVIRONMENT: "Development",
    ConnectionStrings__Workflow: `Host=127.0.0.1;Port=${port};Database=workflow;Username=workflow;Password=${password}`,
    Engine__Port: "4285",
    Api__Port: "4284",
    Storage__Root: resolve(storage, "blobs"),
    Storage__UploadHmacKey: randomBytes(32).toString("base64"),
    VITE_AUTH_MODE: "development",
    KARDIA_API_URL: api,
  }
  run("dotnet", [host, "--migrate"], environment)
  start("dotnet", [host, "--dev"], environment)
  await ready(`${api}/healthz/leader`)
  start(
    process.execPath,
    [
      resolve(root, "frontend", "node_modules", "vite", "bin", "vite.js"),
      "--host",
      "127.0.0.1",
      "--port",
      "4283",
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
      pids: children.map((child) => child.pid),
    })
  )
  console.log(`Clone stack ready: ${origin}; API ${api}; stop: ${stopFile}`)
  while (!shutdown.signal.aborted) {
    if (children.some((child) => child.exitCode !== null)) {
      throw new Error(`Clone stack exited.\n${log}`)
    }
    await delay(200)
  }
} finally {
  unwatchFile(stopFile)
  for (const child of children.reverse()) {
    if (child.exitCode === null) {
      child.kill("SIGTERM")
      await Promise.race([
        new Promise<void>((done) => child.once("exit", () => done())),
        delay(5_000),
      ])
      if (child.exitCode === null) {
        child.kill("SIGKILL")
      }
    }
  }
  if (created) {
    run("docker", ["rm", "--force", container])
  }
}
