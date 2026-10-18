import { Blob as NodeBlob } from "node:buffer"
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import PdfReport from "./PdfReport"

const { getDocument } = vi.hoisted(() => ({ getDocument: vi.fn() }))
vi.mock("pdfjs-dist", () => ({
  getDocument,
  GlobalWorkerOptions: { workerSrc: "" },
  version: "6.4.299",
}))
vi.mock("pdfjs-dist/build/pdf.worker.min.mjs?url", () => ({
  default: "/assets/pdf.worker.mjs",
}))

function page(width = 600, height = 800, text = "Readable page text") {
  return {
    cleanup: vi.fn(),
    getViewport: vi.fn(({ scale }: { scale: number }) => ({
      width: width * scale,
      height: height * scale,
    })),
    render: vi.fn(() => ({ promise: Promise.resolve(), cancel: vi.fn() })),
    streamTextContent: vi.fn(
      () =>
        new ReadableStream({
          start(controller) {
            controller.enqueue({
              items: [{ str: text }, { type: "endMarkedContent" }],
            })
            controller.close()
          },
        })
    ),
  }
}
let first: ReturnType<typeof page>
let second: ReturnType<typeof page>
let document: {
  numPages: number
  fingerprints: string[]
  getPage: ReturnType<typeof vi.fn>
}
let task: {
  promise: Promise<typeof document>
  destroy: ReturnType<typeof vi.fn>
}

describe("owned PDF.js document and page canvases", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    vi.stubGlobal("Blob", NodeBlob)
    first = page()
    second = page(600, 800, "Second page text")
    document = {
      numPages: 2,
      fingerprints: ["fingerprint"],
      getPage: vi.fn(async (number: number) => (number === 1 ? first : second)),
    }
    task = {
      promise: Promise.resolve(document),
      destroy: vi.fn(async () => {}),
    }
    getDocument.mockReturnValue(task)
  })
  afterEach(() => vi.unstubAllGlobals())
  it("loads only authenticated data, serves bundled assets and exposes exact accessible page text", async () => {
    const content = new Blob(["%PDF-fixture"])
    const view = render(<PdfReport title="Document" content={content} />)
    expect(
      await screen.findByRole("img", { name: "Document: page 1 of 2" })
    ).toBeVisible()
    expect(getDocument).toHaveBeenCalledWith(
      expect.objectContaining({
        data: expect.any(Uint8Array),
        stopAtErrors: true,
        enableXfa: false,
        canvasMaxAreaInBytes: 16_000_000,
        standardFontDataUrl: "/assets/pdfjs/6.4.299/standard_fonts/",
        wasmUrl: "/assets/pdfjs/6.4.299/wasm/",
      })
    )
    expect(
      screen.getByRole("button", { name: "Previous PDF page" })
    ).toBeDisabled()
    fireEvent.click(screen.getByText("PDF page text"))
    expect(screen.getByText("Readable page text")).toBeVisible()
    fireEvent.click(screen.getByRole("button", { name: "Next PDF page" }))
    expect(
      await screen.findByRole("img", { name: "Document: page 2 of 2" })
    ).toBeVisible()
    expect(screen.queryByText("Readable page text")).toBeNull()
    expect(screen.getByRole("button", { name: "Next PDF page" })).toBeDisabled()
    expect(first.cleanup).toHaveBeenCalled()
    expect(first.render.mock.results[0]!.value.cancel).toHaveBeenCalled()
    view.unmount()
    expect(second.cleanup).toHaveBeenCalled()
    expect(task.destroy).toHaveBeenCalledOnce()
  })
  it("caps huge page raster allocations without losing the text alternative", async () => {
    first = page(100_000, 100_000)
    render(<PdfReport title="Huge" content={new Blob(["%PDF"])} />)
    const canvas = await screen.findByRole("img")
    expect(canvas).toBeInstanceOf(HTMLCanvasElement)
    if (!(canvas instanceof HTMLCanvasElement))
      throw new Error("Missing PDF canvas.")
    expect(canvas.width * canvas.height).toBeLessThanOrEqual(4_000_000)
    expect(Math.max(canvas.width, canvas.height)).toBeLessThanOrEqual(8192)
  })
  it("surfaces invalid document failures rather than retaining an old canvas", async () => {
    const view = render(
      <PdfReport title="Valid" content={new Blob(["%PDF"])} />
    )
    await screen.findByRole("img")
    getDocument.mockReturnValue({
      promise: Promise.reject(new Error("Invalid PDF document.")),
      destroy: vi.fn(async () => {}),
    })
    view.rerender(<PdfReport title="Invalid" content={new Blob(["broken"])} />)
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Invalid PDF document."
    )
    expect(screen.queryByRole("img")).toBeNull()
  })
  it("rejects invalid page dimensions before allocating a canvas", async () => {
    first = page(Number.POSITIVE_INFINITY, 100)
    render(<PdfReport title="Invalid" content={new Blob(["%PDF"])} />)
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "dimensions are invalid"
    )
    expect(first.render).not.toHaveBeenCalled()
  })
  it("ignores late document completion and destroys the owned loading task", async () => {
    let finish: ((result: typeof document) => void) | undefined
    task.promise = new Promise((resolve) => {
      finish = resolve
    })
    const view = render(
      <PdfReport title="Pending" content={new Blob(["%PDF"])} />
    )
    await waitFor(() => expect(getDocument).toHaveBeenCalledOnce())
    view.unmount()
    expect(task.destroy).toHaveBeenCalledOnce()
    await act(async () => finish?.(document))
    expect(document.getPage).not.toHaveBeenCalled()
  })
  it("surfaces content read failures before starting PDF.js", async () => {
    const content = new Blob(["%PDF"])
    vi.spyOn(content, "arrayBuffer").mockRejectedValue(
      new Error("Read failed.")
    )
    render(<PdfReport title="Unreadable" content={content} />)
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "PDF content could not be read."
    )
    expect(getDocument).not.toHaveBeenCalled()
  })
})
