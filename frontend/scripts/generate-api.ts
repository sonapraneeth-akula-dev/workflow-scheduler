import { readFile, writeFile } from "node:fs/promises"
import openapiTS, { astToString } from "openapi-typescript"

const base = process.env.KARDIA_API_URL ?? "http://127.0.0.1:5001"
const check = process.argv.includes("--check")
for (const [document, destination] of [
  ["v1", "schema.d.ts"],
  ["ui", "ui-schema.d.ts"],
]) {
  const response = await fetch(`${base}/openapi/${document}.json`, {
    headers: process.env.KARDIA_API_TOKEN
      ? { Authorization: `Bearer ${process.env.KARDIA_API_TOKEN}` }
      : {},
  })
  if (!response.ok) {
    throw new Error(
      `Cannot read ${document} OpenAPI document: HTTP ${response.status}.`
    )
  }
  const source = await response.text()
  const generated = astToString(
    await openapiTS(source, { emptyObjectsUnknown: true })
  )
  const path = new URL(`../src/lib/api/${destination}`, import.meta.url)
  if (check) {
    if ((await readFile(path, "utf8")) !== generated) {
      throw new Error(
        `${destination} differs from the running API. Run bun run api:generate and review the change.`
      )
    }
  } else {
    await writeFile(path, generated)
  }
}
