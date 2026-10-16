import { parseTable } from "./parseTable"
import type { TableRequest, TableResult } from "./types"

addEventListener("message", (event: MessageEvent<TableRequest>) => {
  void event.data.content.arrayBuffer().then(
    (bytes) => {
      let result: TableResult
      try {
        let text: string
        try {
          text = new TextDecoder("utf-8", { fatal: true }).decode(bytes)
        } catch {
          throw new Error("Table report is not valid UTF-8.")
        }
        result = { data: parseTable(text, event.data.format) }
      } catch (error) {
        result = {
          error: error instanceof Error ? error.message : String(error),
        }
      }
      postMessage(result)
    },
    () => postMessage({ error: "Table report content could not be read." })
  )
})
