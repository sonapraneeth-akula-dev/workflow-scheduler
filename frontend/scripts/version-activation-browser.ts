import assert from "node:assert/strict"
import { spawn } from "node:child_process"
import { rm } from "node:fs/promises"
import { resolve } from "node:path"
import { createServer } from "vite"
import { z } from "zod"

process.env.KARDIA_API_URL = z.url().parse(process.env.WF_ACTIVATION_API)
process.env.VITE_AUTH_MODE = "entra"
process.env.VITE_ENTRA_TENANT_ID = "ef8a05a0-451c-4479-9c8d-391b6bc7ee1a"
process.env.VITE_ENTRA_SPA_CLIENT_ID = "60d24b19-f65a-4ed4-96cc-9174bfcacabc"
process.env.VITE_ENTRA_API_SCOPE =
  "api://562b0870-4522-43da-8587-41e9b788f309/api.read"
const cacheDir = resolve(
  "node_modules",
  `.vite-version-activation-${process.pid}`
)
const server = await createServer({
  cacheDir,
  server: { port: 0, strictPort: false },
})
try {
  await server.listen()
  const address = server.httpServer?.address()
  assert(address && typeof address !== "string", "Vite did not bind a port.")
  const child = spawn(
    "bun",
    ["x", "--no-install", "playwright", "test", "version-activation.spec.ts"],
    {
      stdio: "inherit",
      env: {
        ...process.env,
        WF_ACTIVATION_ORIGIN: `http://127.0.0.1:${address.port}`,
      },
    }
  )
  process.exitCode = await new Promise<number>((resolve, reject) => {
    child.once("error", reject)
    child.once("exit", (code, signal) =>
      signal
        ? reject(
            new Error(`Version activation Playwright exited with ${signal}.`)
          )
        : resolve(code ?? 1)
    )
  })
} finally {
  await server.close()
  await rm(cacheDir, { recursive: true, force: true })
}
