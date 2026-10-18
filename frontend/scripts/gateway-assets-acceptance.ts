import assert from "node:assert/strict"
import { execFile } from "node:child_process"
import { randomUUID } from "node:crypto"
import {
  copyFile,
  mkdir,
  mkdtemp,
  readFile,
  readdir,
  rm,
  writeFile,
} from "node:fs/promises"
import { dirname, resolve } from "node:path"
import { setTimeout } from "node:timers/promises"
import { promisify } from "node:util"
import { chromium, firefox, webkit, expect } from "@playwright/test"
import type { Browser } from "@playwright/test"
import { build } from "vite"

const repository = resolve(import.meta.dirname, "../..")
const artifacts = resolve(repository, ".agent-artifacts")
await mkdir(artifacts, { recursive: true })
const root = await mkdtemp(resolve(artifacts, "gateway-assets-"))
const id = randomUUID().replaceAll("-", "")
const images = ["first", "old", "current", "next"].map(
  (release) => `wf-gateway-assets:${id}-${release}`
)
const [firstImage, oldImage, currentImage, nextImage] = images
assert(firstImage && oldImage && currentImage && nextImage)
const built = new Set<string>()
const containers = new Set<string>()
const browsers: Browser[] = []
const errors: unknown[] = []
const execute = promisify(execFile)
const deadline = AbortSignal.timeout(15 * 60 * 1000)
let port: string | undefined

async function docker(args: string[], cleanup = false) {
  const { stdout } = await execute("docker", args, {
    cwd: repository,
    signal: cleanup ? AbortSignal.timeout(30_000) : deadline,
    maxBuffer: 8 * 1024 * 1024,
    encoding: "utf8",
  })
  return stdout.trim()
}

async function buildGateway(image: string, previous?: string) {
  await docker([
    "build",
    "--quiet",
    "--file",
    "infra/local/gateway.Dockerfile",
    "--tag",
    image,
    ...(previous ? ["--build-arg", `PREVIOUS_GATEWAY_IMAGE=${previous}`] : []),
    ".",
  ])
  built.add(image)
}

async function start(image: string) {
  const container = await docker([
    "run",
    "--detach",
    "--publish",
    `127.0.0.1:${port ?? ""}:8080`,
    image,
  ])
  assert.match(container, /^[a-f0-9]{64}$/)
  containers.add(container)
  const binding = await docker(["port", container, "8080/tcp"])
  const match = /^127\.0\.0\.1:(\d+)$/.exec(binding)
  assert(match?.[1], `Unexpected container binding: ${binding}`)
  port = match[1]
  const origin = `http://127.0.0.1:${port}`
  for (let attempt = 0; attempt < 100; attempt++) {
    deadline.throwIfAborted()
    assert.equal(
      await docker(["inspect", "--format", "{{.State.Running}}", container]),
      "true",
      await docker(["logs", container])
    )
    try {
      const response = await fetch(origin, {
        signal: AbortSignal.timeout(1_000),
      })
      if (response.ok) return { container, origin }
    } catch (error) {
      if (
        !(error instanceof TypeError) &&
        !(error instanceof DOMException && error.name === "TimeoutError")
      )
        throw error
    }
    await setTimeout(100)
  }
  throw new Error(
    `Gateway did not become responsive: ${await docker(["logs", container])}`
  )
}

async function stop(container: string) {
  await docker(["rm", "--force", container])
  containers.delete(container)
}

function immutable(response: Response) {
  assert.match(response.headers.get("cache-control") ?? "", /\bpublic\b/)
  assert.match(
    response.headers.get("cache-control") ?? "",
    /\bmax-age=31536000\b/
  )
  assert.match(response.headers.get("cache-control") ?? "", /\bimmutable\b/)
  assert.equal(response.headers.get("x-content-type-options"), "nosniff")
}

async function currentHeaders(origin: string) {
  let html = ""
  for (const path of ["/", "/index.html", "/runs/deploy-safe-deep-link"]) {
    const response = await fetch(origin + path, { signal: deadline })
    assert.equal(response.status, 200)
    assert.match(response.headers.get("cache-control") ?? "", /\bno-cache\b/)
    assert.match(response.headers.get("cache-control") ?? "", /\bno-store\b/)
    html = await response.text()
    assert.match(html, /<div id="root">/)
    assert(!html.includes("old-release-fixture"))
  }
  const asset = /src="(\/assets\/[^"]+-[a-zA-Z0-9_-]+\.js)"/.exec(html)?.[1]
  assert(asset, "Production Vite entry must have a hashed asset filename.")
  const script = await fetch(origin + asset, {
    method: "HEAD",
    signal: deadline,
  })
  assert.equal(script.status, 200)
  immutable(script)
  assert.match(script.headers.get("content-type") ?? "", /javascript/)
  const etag = script.headers.get("etag")
  assert(etag)
  const cached = await fetch(origin + asset, {
    headers: { "If-None-Match": etag },
    signal: deadline,
  })
  assert.equal(cached.status, 304)
  immutable(cached)
  const range = await fetch(origin + asset, {
    headers: { Range: "bytes=0-15" },
    signal: deadline,
  })
  assert.equal(range.status, 206)
  immutable(range)
  assert.equal((await range.arrayBuffer()).byteLength, 16)
  for (const path of [
    "/assets/not-a-real-chunk.js",
    "/assets/missing/style.css",
    "/assets/",
  ]) {
    const missing = await fetch(origin + path, { signal: deadline })
    assert.equal(
      missing.status,
      404,
      `Missing asset must not return SPA HTML: ${path}`
    )
    assert(!missing.headers.get("cache-control")?.includes("immutable"))
    assert(!(await missing.text()).includes('<div id="root">'))
  }
}

try {
  await buildGateway(firstImage)
  let gateway = await start(firstImage)
  await currentHeaders(gateway.origin)
  await stop(gateway.container)

  const fixture = resolve(root, "fixture")
  await mkdir(fixture)
  await writeFile(
    resolve(fixture, "index.html"),
    '<!doctype html><title>old-release-fixture</title><button id="load">Open route</button><p id="result">Not loaded</p><script type="module" src="/entry.js"></script>'
  )
  await writeFile(
    resolve(fixture, "entry.js"),
    'document.querySelector("#load").onclick=async()=>{const route=await import("./route.js");document.querySelector("#result").textContent=route.text}'
  )
  await writeFile(
    resolve(fixture, "route.js"),
    'import "./route.css";export const text="Retained old route loaded";'
  )
  await writeFile(resolve(fixture, "route.css"), "#result{color:rgb(17,34,51)}")
  const dist = resolve(root, "dist")
  await build({
    configFile: false,
    root: fixture,
    logLevel: "warn",
    build: { outDir: dist, emptyOutDir: true },
  })
  const route = (await readdir(resolve(dist, "assets"))).find((name) =>
    /^route-.+\.js$/.test(name)
  )
  assert(route, "Vite must emit a separate hashed lazy route chunk.")
  const oldRoute = `/assets/${route}`
  const originalRoute = await readFile(resolve(dist, "assets", route))
  await copyFile(
    resolve(repository, "infra/local/Caddyfile.gateway"),
    resolve(root, "Caddyfile")
  )
  await writeFile(
    resolve(root, "Dockerfile"),
    [
      "FROM caddy:2.11.6-alpine",
      "COPY Caddyfile /etc/caddy/Caddyfile",
      "COPY dist /srv",
      "USER 1000:1000",
    ].join("\n")
  )
  await docker(["build", "--quiet", "--tag", oldImage, root])
  built.add(oldImage)
  gateway = await start(oldImage)
  const pages = []
  for (const engine of [chromium, firefox, webkit]) {
    const browser = await engine.launch()
    browsers.push(browser)
    const page = await browser.newPage()
    const requests: string[] = []
    const failures: string[] = []
    page.on("request", (request) =>
      requests.push(new URL(request.url()).pathname)
    )
    page.on("pageerror", (error) => failures.push(error.message))
    await page.goto(gateway.origin)
    await expect(page.getByText("Not loaded", { exact: true })).toBeVisible()
    assert(
      !requests.includes(oldRoute),
      "Lazy route was requested before deployment."
    )
    pages.push({ page, requests, failures, engine: engine.name() })
  }

  await buildGateway(currentImage, oldImage)
  await stop(gateway.container)
  gateway = await start(currentImage)
  await currentHeaders(gateway.origin)
  for (const { page, requests, failures, engine } of pages) {
    await page.getByRole("button", { name: "Open route", exact: true }).click()
    await expect(
      page.getByText("Retained old route loaded", { exact: true })
    ).toHaveCSS("color", "rgb(17, 34, 51)")
    assert(
      requests.includes(oldRoute),
      "Old tab must fetch its lazy chunk after deployment."
    )
    assert.deepEqual(failures, [])
    console.log(
      `${engine}: old tab fetched its original hashed lazy JS/CSS after production image deployment.`
    )
  }
  const retained = await fetch(gateway.origin + oldRoute, { signal: deadline })
  assert.equal(retained.status, 200)
  immutable(retained)
  assert.deepEqual(Buffer.from(await retained.arrayBuffer()), originalRoute)

  await buildGateway(nextImage, currentImage)
  await stop(gateway.container)
  gateway = await start(nextImage)
  await currentHeaders(gateway.origin)
  const expired = await fetch(gateway.origin + oldRoute, { signal: deadline })
  assert.equal(
    expired.status,
    404,
    "Retain one prior release, not an accumulating history."
  )
  assert(!expired.headers.get("cache-control")?.includes("immutable"))
  console.log(
    "Actual production image: first install, current/prior assets, HEAD/206/304 caching, index/deep-link revalidation, missing-asset 404 and one-release retention passed."
  )
} catch (error) {
  errors.push(error)
} finally {
  for (const browser of browsers) {
    try {
      await browser.close()
    } catch (error) {
      errors.push(error)
    }
  }
  for (const container of containers) {
    try {
      await docker(["rm", "--force", container], true)
    } catch (error) {
      errors.push(error)
    }
  }
  for (const image of [...built].reverse()) {
    try {
      await docker(["image", "rm", image], true)
    } catch (error) {
      errors.push(error)
    }
  }
  assert.equal(dirname(root), artifacts)
  try {
    await rm(root, { recursive: true, force: true })
  } catch (error) {
    errors.push(error)
  }
}
if (errors.length)
  throw new AggregateError(
    errors,
    "Gateway asset deployment acceptance failed."
  )
