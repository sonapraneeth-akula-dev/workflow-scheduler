import { readFile, writeFile, stat } from "node:fs/promises"
import { setTimeout as delay } from "node:timers/promises"
import { tmpdir } from "node:os"
import { resolve } from "node:path"
import { z } from "zod"

export default async function stopTestStack(): Promise<void> {
  const stateFile = new URL("../.test-stack.json", import.meta.url)
  let state: string
  try {
    state = await readFile(stateFile, "utf8")
  } catch (error) {
    if (error instanceof Error && "code" in error && error.code === "ENOENT") {
      return
    }
    throw error
  }
  const { directory } = z
    .object({ directory: z.string().regex(/^wf-ui-[a-zA-Z0-9]{6}$/) })
    .parse(JSON.parse(state))
  await writeFile(resolve(tmpdir(), directory, "stop"), "")
  for (let attempt = 0; attempt < 300; attempt++) {
    try {
      await stat(stateFile)
    } catch (error) {
      if (
        error instanceof Error &&
        "code" in error &&
        error.code === "ENOENT"
      ) {
        return
      }
      throw error
    }
    await delay(100)
  }
  throw new Error(
    "UI fixture did not finish cleaning up its host, Vite, storage and PostgreSQL container."
  )
}
