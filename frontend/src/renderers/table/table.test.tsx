import { act, fireEvent, render, screen, within } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { parseTable } from "./parseTable"
import { TableContent } from "./TableReport"
import type { TableResult } from "./types"
import { VirtualTable } from "./VirtualTable"

describe("strict table payloads", () => {
  it("parses CSV with quoted commas, newlines, quotes and literal numeric/formula cells", () => {
    expect(
      parseTable(
        'name,note\r\n"one, two","first\nsecond"\r\nthree,"a ""quote"""\r\n001,=1+1',
        "csv"
      )
    ).toEqual({
      columns: ["name", "note"],
      rows: [
        ["one, two", "first\nsecond"],
        ["three", 'a "quote"'],
        ["001", "=1+1"],
      ],
    })
  })
  it("retains exact scalar table cells, nulls and order", () => {
    expect(
      parseTable(
        JSON.stringify({
          columns: ["name", "value"],
          rows: [
            ["<script>", 2],
            [true, null],
          ],
        }),
        "auto"
      )
    ).toEqual({
      columns: ["name", "value"],
      rows: [
        ["<script>", "2"],
        ["true", ""],
      ],
    })
  })
  it("unions record columns in stable order without interpreting inherited names", () => {
    expect(
      parseTable(
        '[{"__proto__":"literal","count":0},{"constructor":"own"}]',
        "auto"
      )
    ).toEqual({
      columns: ["__proto__", "count", "constructor"],
      rows: [
        ["literal", "0", ""],
        ["", "", "own"],
      ],
    })
  })
  it("supports empty CSV/JSON and forces ambiguous CSV headers through csv kind", () => {
    expect(parseTable("", "auto")).toEqual({ columns: [], rows: [] })
    expect(parseTable("[]", "auto")).toEqual({ columns: [], rows: [] })
    expect(parseTable("[group]\nvalue", "csv")).toEqual({
      columns: ["[group]"],
      rows: [["value"]],
    })
  })
  it.each([
    'a,b\n"unclosed,b',
    "a,b\none",
    "a,b\none,two,three",
    "{invalid",
    '[{"nested":{"not":"a cell"}}]',
    '[{"value":1e400}]',
    "[{}]",
    '{"columns":["a"],"rows":[["one","extra"]]}',
    JSON.stringify({
      columns: Array.from({ length: 129 }, () => "a"),
      rows: [],
    }),
  ])("rejects malformed or unbounded table shape: %s", (text) => {
    expect(() => parseTable(text, "auto")).toThrow()
  })
})

describe("accessible virtual table", () => {
  it("bounds mounted rows and exposes distant rows through actual scroll state", () => {
    const rows = Array.from({ length: 1000 }, (_, index) => [`item-${index}`])
    const view = render(
      <VirtualTable columns={["name"]} rows={rows} title="Results" />
    )
    expect(screen.getByRole("table")).toHaveAttribute("aria-rowcount", "1001")
    expect(
      view.container.querySelectorAll("tbody tr[aria-rowindex]")
    ).toHaveLength(24)
    expect(screen.queryByText("item-495")).toBeNull()
    fireEvent.scroll(
      screen.getByRole("region", { name: "Results scrollable data" }),
      { target: { scrollTop: 32 * 500 } }
    )
    expect(screen.getByText("item-495")).toBeVisible()
    expect(screen.queryByText("item-0")).toBeNull()
    fireEvent.change(screen.getByRole("searchbox"), {
      target: { value: "item-999" },
    })
    expect(screen.getByText("item-999")).toBeVisible()
    expect(screen.getByRole("status")).toHaveTextContent("1 of 1,000 rows")
  })
  it("sorts naturally in both directions, filters and renders HTML-looking cells as text", () => {
    const view = render(
      <VirtualTable
        columns={["name", "count"]}
        rows={[
          ["<img onerror=alert()>", "10"],
          ["two", "2"],
          ["one", "1"],
        ]}
        title="Counts"
      />
    )
    fireEvent.change(screen.getByRole("combobox"), { target: { value: "1" } })
    const values = () =>
      Array.from(
        view.container.querySelectorAll(
          "tbody tr[aria-rowindex] td:nth-child(2)"
        )
      ).map((cell) => cell.textContent)
    expect(values()).toEqual(["1", "2", "10"])
    fireEvent.click(screen.getByRole("button", { name: "Ascending" }))
    expect(values()).toEqual(["10", "2", "1"])
    expect(view.container.querySelector("img")).toBeNull()
    fireEvent.change(screen.getByRole("searchbox"), {
      target: { value: "not present" },
    })
    expect(screen.getByText("No matching table rows.")).toBeVisible()
    expect(
      within(screen.getByRole("table")).queryAllByRole("cell")
    ).toHaveLength(0)
  })
})

const workers: FakeWorker[] = []
class FakeWorker {
  readonly url: URL
  readonly options: WorkerOptions
  onmessage: ((event: MessageEvent<TableResult>) => void) | null = null
  onerror: ((event: ErrorEvent) => void) | null = null
  postMessage = vi.fn()
  terminate = vi.fn()
  constructor(url: URL, options: WorkerOptions) {
    this.url = url
    this.options = options
    workers.push(this)
  }
}

describe("owned table parser worker", () => {
  beforeEach(() => {
    workers.length = 0
    vi.stubGlobal("Worker", FakeWorker)
  })
  afterEach(() => vi.unstubAllGlobals())
  it("starts a module worker with the original Blob and renders its validated result", () => {
    const content = new Blob(["name\nvalue"])
    const view = render(
      <TableContent title="CSV" content={content} format="csv" />
    )
    expect(screen.getByRole("status")).toHaveTextContent("Parsing table")
    const worker = workers[0]!
    expect(worker.options).toEqual({ type: "module" })
    expect(worker.url.pathname).toMatch(/table\.worker\.ts$/)
    expect(worker.postMessage).toHaveBeenCalledWith({ content, format: "csv" })
    act(() =>
      worker.onmessage?.(
        new MessageEvent("message", {
          data: { data: { columns: ["name"], rows: [["value"]] } },
        })
      )
    )
    expect(screen.getByRole("cell")).toHaveTextContent("value")
    expect(worker.terminate).toHaveBeenCalled()
    view.unmount()
    expect(worker.terminate).toHaveBeenCalledTimes(2)
  })
  it("ignores a queued response after replacement and terminates every owned worker", () => {
    const view = render(
      <TableContent title="First" content={new Blob(["a"])} format="auto" />
    )
    const first = workers[0]!
    view.rerender(
      <TableContent title="Second" content={new Blob(["b"])} format="csv" />
    )
    act(() =>
      first.onmessage?.(
        new MessageEvent("message", {
          data: { data: { columns: ["old"], rows: [["stale"]] } },
        })
      )
    )
    expect(screen.queryByText("stale")).toBeNull()
    expect(screen.getByRole("status")).toHaveTextContent("Parsing table")
    expect(first.terminate).toHaveBeenCalled()
    view.unmount()
    expect(workers[1]!.terminate).toHaveBeenCalled()
  })
  it("surfaces explicit parse and native worker failures", () => {
    render(<TableContent title="Bad" content={new Blob(["a"])} format="auto" />)
    act(() =>
      workers[0]!.onmessage?.(
        new MessageEvent("message", { data: { error: "Invalid CSV report." } })
      )
    )
    expect(screen.getByRole("alert")).toHaveTextContent("Invalid CSV report.")
    const event = new ErrorEvent("error", { cancelable: true })
    act(() => workers[0]!.onerror?.(event))
    expect(screen.getByRole("alert")).toHaveTextContent(
      "Table parser worker failed."
    )
    expect(event.defaultPrevented).toBe(true)
  })
  it("reports unavailable native workers instead of falling back to main-thread parsing", () => {
    vi.stubGlobal(
      "Worker",
      class {
        constructor() {
          throw new Error("Workers unavailable.")
        }
      }
    )
    render(
      <TableContent title="Blocked" content={new Blob(["a"])} format="auto" />
    )
    expect(screen.getByRole("alert")).toHaveTextContent("Workers unavailable.")
  })
})
