import { readFileSync, readdirSync } from "node:fs"
import { resolve } from "node:path"
import { z } from "zod"
import type { Plugin } from "vite"

export function pdfAssets(): Plugin {
  const root = resolve(import.meta.dirname, "..", "node_modules", "pdfjs-dist")
  const { version } = z
    .object({ version: z.string() })
    .parse(JSON.parse(readFileSync(resolve(root, "package.json"), "utf8")))
  const prefix = `assets/pdfjs/${version}/`
  const files = new Set([
    "LICENSE",
    ...["cmaps", "standard_fonts", "wasm", "iccs"].flatMap((directory) =>
      readdirSync(resolve(root, directory)).map(
        (filename) => `${directory}/${filename}`
      )
    ),
  ])
  return {
    name: "bundled-pdfjs-assets",
    configureServer(server) {
      const base = new URL(server.config.base, "http://localhost").pathname
      const route = `${base}${prefix}`
      server.middlewares.use((request, response, next) => {
        const pathname = new URL(request.url ?? "/", "http://localhost")
          .pathname
        const name = pathname.slice(route.length)
        if (!pathname.startsWith(route) || !files.has(name)) return next()
        response.setHeader(
          "Content-Type",
          name.endsWith(".wasm")
            ? "application/wasm"
            : name.endsWith(".js")
              ? "text/javascript"
              : "application/octet-stream"
        )
        response.setHeader("X-Content-Type-Options", "nosniff")
        response.end(readFileSync(resolve(root, ...name.split("/"))))
      })
    },
    generateBundle() {
      for (const name of files)
        this.emitFile({
          type: "asset",
          fileName: `${prefix}${name}`,
          source: readFileSync(resolve(root, ...name.split("/"))),
        })
    },
  }
}
