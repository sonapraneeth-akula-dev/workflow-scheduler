import Papa from "papaparse"
import { parseTable, validateTable } from "./parseTable"
import type { TableData, TableRequest, TableResult } from "./types"

const pages: TableData[] = []
let parser: Papa.Parser | undefined
let parsedRows = 0
let complete = false

function sendPage(page: number) {
  const data = pages[page]
  if (!data) throw new Error("Requested table page is unavailable.")
  postMessage({
    data,
    pagination: { page, pageCount: pages.length, parsedRows, complete },
  } satisfies TableResult)
}

function addPage(data: TableData, done: boolean) {
  validateTable(data)
  parsedRows += data.rows.length
  complete = done
  pages.push(data)
  sendPage(pages.length - 1)
}

function parsePages(text: string, format: "auto" | "csv") {
  if (format === "auto" && /^[\s]*[[{]/.test(text)) {
    const data = parseTable(text, format)
    for (let start = 0; start < Math.max(1, data.rows.length); start += 1000)
      pages.push({
        columns: data.columns,
        rows: data.rows.slice(start, start + 1000),
      })
    parsedRows = data.rows.length
    complete = true
    sendPage(0)
    return
  }
  let columns: string[] | undefined
  const config: Papa.ParseLocalConfig<string[]> & {
    worker: false
    download: false
  } = {
    worker: false,
    download: false,
    delimiter: ",",
    skipEmptyLines: true,
    dynamicTyping: false,
    chunkSize: 128 * 1024,
    chunk(result, handle) {
      parser = handle
      try {
        if (result.errors.length)
          throw new Error(`Invalid CSV report: ${result.errors[0]!.code}.`)
        columns ??= result.data.shift()
        const done = result.meta.cursor === text.length
        if (!result.data.length && !done) return
        addPage({ columns: columns ?? [], rows: result.data }, done)
        if (!done) handle.pause()
      } catch (error) {
        handle.abort()
        postMessage({
          error: error instanceof Error ? error.message : String(error),
        } satisfies TableResult)
      }
    },
    complete() {},
  }
  Papa.parse(text, config)
}

addEventListener("message", (event: MessageEvent<TableRequest>) => {
  const request = event.data
  if ("page" in request) {
    try {
      if (!Number.isInteger(request.page) || request.page < 0)
        throw new Error("Requested table page is invalid.")
      if (request.page < pages.length) sendPage(request.page)
      else if (request.page === pages.length && !complete && parser)
        parser.resume()
      else throw new Error("Requested table page is unavailable.")
    } catch (error) {
      postMessage({
        error: error instanceof Error ? error.message : String(error),
      } satisfies TableResult)
    }
    return
  }
  void request.content.arrayBuffer().then(
    (bytes) => {
      let result: TableResult
      try {
        let text: string
        try {
          text = new TextDecoder("utf-8", { fatal: true }).decode(bytes)
        } catch {
          throw new Error("Table report is not valid UTF-8.")
        }
        if (request.content.size > 5_000_000) {
          parsePages(text, request.format)
          return
        }
        result = { data: parseTable(text, request.format) }
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
