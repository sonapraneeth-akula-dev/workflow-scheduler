import { Blob as NodeBlob } from "node:buffer"
import { waitFor } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import type { TableRequest, TableResult } from "./types"

let receive: (event: MessageEvent<TableRequest>) => void
const sent = vi.fn<(result: TableResult) => void>()
const csvRow = "name," + "x".repeat(195) + "\n"

async function response(index: number) {
  await waitFor(() => expect(sent.mock.calls.length).toBeGreaterThan(index))
  return sent.mock.calls[index]![0]
}
function send(request: TableRequest) {
  receive(new MessageEvent("message", { data: request }))
}

describe("actual Papa Parse worker protocol", () => {
  beforeEach(async () => {
    vi.resetModules()
    sent.mockClear()
    vi.stubGlobal("Blob", NodeBlob)
    vi.stubGlobal("postMessage", sent)
    vi.stubGlobal(
      "addEventListener",
      (_name: string, listener: typeof receive) => {
        receive = listener
      }
    )
    await import("./table.worker")
  })
  afterEach(() => vi.unstubAllGlobals())

  it("preserves the small complete-response contract and fatal UTF-8 errors", async () => {
    send({ content: new Blob(["a,b\none,two"]), format: "csv" })
    expect(await response(0)).toEqual({
      data: { columns: ["a", "b"], rows: [["one", "two"]] },
    })
    send({ content: new Blob([Uint8Array.of(255)]), format: "csv" })
    expect(await response(1)).toEqual({
      error: "Table report is not valid UTF-8.",
    })
  })

  it("pauses large CSV after a bounded page and serves previous pages without reparsing", async () => {
    send({ content: new Blob(["a,b\n", csvRow.repeat(26_000)]), format: "csv" })
    const first = await response(0)
    if (!("data" in first)) throw new Error(first.error)
    expect(first.pagination).toMatchObject({
      page: 0,
      pageCount: 1,
      complete: false,
    })
    expect(first.data.rows.length).toBeGreaterThan(1)
    expect(first.data.rows.length).toBeLessThan(1000)
    await new Promise((resolve) => setTimeout(resolve, 10))
    expect(sent).toHaveBeenCalledTimes(1)
    send({ page: 1 })
    const next = await response(1)
    if (!("data" in next)) throw new Error(next.error)
    expect(next.pagination).toMatchObject({
      page: 1,
      pageCount: 2,
      complete: false,
    })
    expect(next.data.columns).toEqual(["a", "b"])
    send({ page: 0 })
    expect(await response(2)).toMatchObject({
      data: first.data,
      pagination: { page: 0, pageCount: 2 },
    })
  })

  it("preserves quoted multiline Unicode spanning parser chunks and reports exact completion", async () => {
    const largeCell = `${"a".repeat(131_070)},\n"\u03bb"`
    const quoted = `"${largeCell.replaceAll('"', '""')}",value\n`
    send({
      content: new Blob(["a,b\n", quoted, csvRow.repeat(26_000)]),
      format: "csv",
    })
    let result = await response(0)
    if (!("data" in result)) throw new Error(result.error)
    expect(result.data.rows[0]).toEqual([largeCell, "value"])
    let calls = 1
    while (!result.pagination?.complete) {
      send({ page: calls })
      result = await response(calls++)
      if (!("data" in result)) throw new Error(result.error)
    }
    expect(result.pagination?.parsedRows).toBe(26_001)
    expect(result.data.columns).toEqual(["a", "b"])
  })

  it("surfaces malformed late CSV instead of declaring an incomplete preview valid", async () => {
    send({
      content: new Blob(["a,b\n", csvRow.repeat(26_000), "broken\n"]),
      format: "csv",
    })
    let result = await response(0)
    let calls = 1
    while ("data" in result && !result.pagination?.complete) {
      send({ page: calls })
      result = await response(calls++)
    }
    expect(result).toEqual({
      error:
        "Table rows must match the header width, with at most 128 columns.",
    })
  })

  it("pages fully validated large JSON and rejects impossible page requests explicitly", async () => {
    const rows = Array.from({ length: 26_000 }, () => [
      "value",
      "x".repeat(195),
    ])
    send({
      content: new Blob([JSON.stringify({ columns: ["a", "b"], rows })]),
      format: "auto",
    })
    const first = await response(0)
    if (!("data" in first)) throw new Error(first.error)
    expect(first.data.rows).toHaveLength(1000)
    expect(first.pagination).toEqual({
      page: 0,
      pageCount: 26,
      parsedRows: 26_000,
      complete: true,
    })
    send({ page: 25 })
    expect(await response(1)).toMatchObject({
      pagination: { page: 25 },
      data: { rows: rows.slice(25_000) },
    })
    send({ page: -1 })
    expect(await response(2)).toEqual({
      error: "Requested table page is invalid.",
    })
    send({ page: 26 })
    expect(await response(3)).toEqual({
      error: "Requested table page is unavailable.",
    })
  })
})
