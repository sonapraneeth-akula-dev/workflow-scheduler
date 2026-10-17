// @vitest-environment node
import { execFile } from "node:child_process"
import {
  copyFile,
  mkdir,
  mkdtemp,
  readFile,
  rm,
  writeFile,
} from "node:fs/promises"
import { createServer } from "node:http"
import { join } from "node:path"
import { fileURLToPath } from "node:url"
import { promisify } from "node:util"
import {
  afterAll,
  afterEach,
  beforeAll,
  beforeEach,
  describe,
  expect,
  it,
} from "vitest"

const execute = promisify(execFile)
const frontend = fileURLToPath(new URL("../../../", import.meta.url))
const token = "api-generation-test-token"
let directory: string
let origin: string
let failingPath: string | undefined
let documents: Record<string, unknown>
const requests: { path: string; authorization: string | undefined }[] = []
function document(path: string) {
  return {
    openapi: "3.1.0",
    info: { title: "Contract fixture", version: "1.0.0" },
    paths: {
      [path]: {
        get: {
          responses: {
            "200": {
              description: "OK",
              content: {
                "application/json": {
                  schema: {
                    type: "object",
                    properties: { id: { type: "string" } },
                  },
                },
              },
            },
          },
        },
      },
    },
  }
}
const server = createServer((request, response) => {
  requests.push({
    path: request.url ?? "",
    authorization: request.headers.authorization,
  })
  response.writeHead(request.url === failingPath ? 503 : 200, {
    "Content-Type": "application/json",
  })
  response.end(JSON.stringify(documents[request.url ?? ""] ?? {}))
})
beforeAll(async () => {
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve))
  const address = server.address()
  if (!address || typeof address === "string")
    throw new Error("Contract server did not bind a TCP port.")
  origin = `http://127.0.0.1:${address.port}`
})
beforeEach(async () => {
  requests.length = 0
  failingPath = undefined
  documents = {
    "/openapi/v1.json": document("/api/v1/audit"),
    "/openapi/ui.json": document("/api/v1/schedules"),
  }
  directory = await mkdtemp(
    join(frontend, "node_modules", ".kardia-api-drift-")
  )
  await Promise.all([
    mkdir(join(directory, "scripts")),
    mkdir(join(directory, "src", "lib", "api"), { recursive: true }),
  ])
  await copyFile(
    join(frontend, "scripts", "generate-api.ts"),
    join(directory, "scripts", "generate-api.ts")
  )
})
afterEach(async () => {
  await rm(directory, { recursive: true, force: true })
})
afterAll(async () => {
  server.closeAllConnections()
  await new Promise<void>((resolve, reject) =>
    server.close((error) => (error ? reject(error) : resolve()))
  )
})
const path = (name: string) => join(directory, "src", "lib", "api", name)
function run(check = false) {
  return execute(
    "bun",
    [
      join(directory, "scripts", "generate-api.ts"),
      ...(check ? ["--check"] : []),
    ],
    {
      cwd: directory,
      timeout: 30_000,
      encoding: "utf8",
      env: { ...process.env, KARDIA_API_URL: origin, KARDIA_API_TOKEN: token },
    }
  )
}
async function contents() {
  return Promise.all(
    ["schema.d.ts", "ui-schema.d.ts"].map((name) =>
      readFile(path(name), "utf8")
    )
  )
}
describe("real API generator process and drift gate", () => {
  it("generates both authenticated 3.1 clients, then checks without writing either", async () => {
    await run()
    const before = await contents()
    expect(before[0]).toContain('"/api/v1/audit"')
    expect(before[0]).not.toContain('"/api/v1/schedules"')
    expect(before[1]).toContain('"/api/v1/schedules"')
    await run(true)
    expect(await contents()).toEqual(before)
    expect(requests.map((request) => request.path)).toEqual([
      "/openapi/v1.json",
      "/openapi/ui.json",
      "/openapi/v1.json",
      "/openapi/ui.json",
    ])
    expect(
      requests.every((request) => request.authorization === `Bearer ${token}`)
    ).toBe(true)
  }, 30_000)
  it.each(["schema.d.ts", "ui-schema.d.ts"])(
    "fails on %s drift and leaves both files unchanged",
    async (name) => {
      await run()
      await writeFile(
        path(name),
        (await readFile(path(name), "utf8")) + "\n// Deliberate drift.\n"
      )
      const before = await contents()
      await expect(run(true)).rejects.toMatchObject({
        code: 1,
        stderr: expect.stringContaining(`${name} differs from the running API`),
      })
      expect(await contents()).toEqual(before)
    },
    30_000
  )
  it("fails when the served API changes without regenerating, not only when a file is edited", async () => {
    await run()
    const before = await contents()
    documents["/openapi/v1.json"] = document("/api/v1/new-operation")
    await expect(run(true)).rejects.toMatchObject({
      code: 1,
      stderr: expect.stringContaining(
        "schema.d.ts differs from the running API"
      ),
    })
    expect(await contents()).toEqual(before)
  }, 30_000)
  it("fails on an unavailable UI contract without writing clients or echoing the bearer token", async () => {
    await run()
    const before = await contents()
    failingPath = "/openapi/ui.json"
    try {
      await run(true)
      expect.fail("An unavailable contract must fail the drift gate.")
    } catch (error) {
      expect(error).toMatchObject({
        code: 1,
        stderr: expect.stringContaining(
          "Cannot read ui OpenAPI document: HTTP 503."
        ),
      })
      expect(String(error)).not.toContain(token)
    }
    expect(await contents()).toEqual(before)
  }, 30_000)
  it("fails when a generated contract file is missing instead of creating it in check mode", async () => {
    await expect(run(true)).rejects.toMatchObject({ code: 1 })
    await expect(readFile(path("schema.d.ts"), "utf8")).rejects.toMatchObject({
      code: "ENOENT",
    })
    await expect(
      readFile(path("ui-schema.d.ts"), "utf8")
    ).rejects.toMatchObject({ code: "ENOENT" })
  }, 30_000)
})
