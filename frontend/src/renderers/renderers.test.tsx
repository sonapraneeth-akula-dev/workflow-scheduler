/// <reference types="node" />
import { Blob as NodeBlob } from "node:buffer"
import { lazy, StrictMode } from "react"
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { ReportRenderer } from "./ReportRenderer"
import { rendererRegistry } from "./registry"
import TextReport from "./text/TextReport"
import MarkdownReport from "./markdown/MarkdownReport"
import JsonReport from "./json/JsonReport"
import ImageReport from "./image/ImageReport"

beforeEach(() => vi.stubGlobal("Blob", NodeBlob))
afterEach(() => vi.unstubAllGlobals())

describe("lazy report registry", () => {
  it.each([
    "markdown",
    "text",
    "json",
    "image",
    "html",
    "text/html",
    "svg",
    "image/svg+xml",
    "table",
    "csv",
    "text/csv",
    "chart",
    "diff",
    "pdf",
    "application/pdf",
  ])("registers %s", (kind) => {
    expect(rendererRegistry.get(kind)).toBeDefined()
  })
  it.each(["HTML", "Svg", "TEXT", "", "__proto__", "constructor"])(
    "does not inherit or reinterpret unknown kind %s",
    (kind) => {
      expect(rendererRegistry.get(kind)).toBeUndefined()
    }
  )
  it("loads the registered component and falls back for an inherited unknown key", async () => {
    vi.stubGlobal(
      "URL",
      class extends URL {
        static createObjectURL = vi.fn(() => "blob:download")
        static revokeObjectURL = vi.fn()
      }
    )
    const content = new Blob(["from lazy renderer"])
    const view = render(
      <ReportRenderer kind="text" content={content} title="Lazy report" />
    )
    expect(await screen.findByRole("region")).toHaveTextContent(
      "from lazy renderer"
    )
    view.rerender(
      <ReportRenderer
        kind="__proto__"
        content={content}
        title="Unknown report"
      />
    )
    expect(
      screen.getByRole("link", { name: "Download Unknown report" })
    ).toHaveAttribute("download", "Unknown report")
  })
  it("isolates renderer failures and recovers when a different report is selected", async () => {
    vi.spyOn(console, "error").mockImplementation(() => undefined)
    function BrokenRenderer(): never {
      throw new Error("Renderer failed.")
    }
    const broken = lazy(async () => ({ default: BrokenRenderer }))
    const lookup = vi.spyOn(rendererRegistry, "get")
    lookup.mockReturnValueOnce(broken)
    const view = render(
      <ReportRenderer kind="broken" content={new Blob()} title="Broken" />
    )
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Renderer failed."
    )
    view.rerender(
      <ReportRenderer
        kind="text"
        content={new Blob(["Recovered"])}
        title="Good"
      />
    )
    expect(await screen.findByRole("region")).toHaveTextContent("Recovered")
    expect(screen.queryByRole("alert")).not.toBeInTheDocument()
  })
})

describe("text report", () => {
  it("preserves whitespace and treats markup, scripts and Unicode as text", async () => {
    const text = 'first\n  second <script>alert("x")</script> \u03bb'
    const { container } = render(
      <TextReport content={new Blob([text])} title="Build output" />
    )
    const region = await screen.findByRole("region", { name: "Build output" })
    expect(region.textContent).toBe(text)
    expect(region).toHaveAttribute("tabindex", "0")
    expect(container.querySelector("script")).toBeNull()
  })
  it("renders an empty payload as an empty report, not an error", async () => {
    render(<TextReport content={new Blob()} title="Empty" />)
    expect(await screen.findByRole("region")).toBeEmptyDOMElement()
  })
  it("rejects malformed UTF-8 rather than corrupting content silently", async () => {
    render(
      <TextReport
        content={new Blob([new Uint8Array([0xc3, 0x28])])}
        title="Invalid text"
      />
    )
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Report content is not valid UTF-8 text."
    )
    expect(screen.queryByRole("region")).not.toBeInTheDocument()
  })
  it("does not show stale content while a replacement Blob is read", async () => {
    const view = render(<TextReport content={new Blob(["old"])} title="Text" />)
    expect(await screen.findByRole("region")).toHaveTextContent("old")
    view.rerender(<TextReport content={new Blob(["new"])} title="Text" />)
    expect(screen.queryByText("old")).not.toBeInTheDocument()
    expect(await screen.findByRole("region")).toHaveTextContent("new")
  })
  it("ignores late reads from a previously selected report", async () => {
    let complete: ((buffer: ArrayBuffer) => void) | undefined
    const old = new Blob(["old"])
    vi.spyOn(old, "arrayBuffer").mockImplementation(
      () =>
        new Promise((resolve) => {
          complete = resolve
        })
    )
    const view = render(<TextReport content={old} title="Text" />)
    view.rerender(<TextReport content={new Blob(["new"])} title="Text" />)
    expect(await screen.findByRole("region")).toHaveTextContent("new")
    await act(async () => complete?.(new TextEncoder().encode("old").buffer))
    expect(screen.getByRole("region")).toHaveTextContent("new")
  })
  it("surfaces Blob read failures", async () => {
    const content = new Blob()
    vi.spyOn(content, "arrayBuffer").mockRejectedValue(
      new Error("private error")
    )
    render(<TextReport content={content} title="Unreadable" />)
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Report content could not be read."
    )
    expect(screen.getByRole("alert")).not.toHaveTextContent("private error")
  })
})

describe("Markdown report", () => {
  it("renders Markdown semantics without raw HTML or active content", async () => {
    const { container } = render(
      <MarkdownReport
        title="Summary"
        content={
          new Blob([
            '# Result\n\n**Passed**\n\n- one\n- two\n\n<img src="/api/v1/private" onerror="alert(1)">\n\n<script>alert(1)</script>\n\n<iframe src="https://example.com"></iframe>',
          ])
        }
      />
    )
    expect(await screen.findByRole("heading", { name: "Result" })).toBeVisible()
    expect(screen.getByText("Passed").tagName).toBe("STRONG")
    expect(screen.getAllByRole("listitem")).toHaveLength(2)
    expect(container.querySelector("img,script,iframe")).toBeNull()
  })
  it("allows only explicit HTTP(S) links with isolation and blocks all image loads", async () => {
    const { container } = render(
      <MarkdownReport
        title="Links"
        content={
          new Blob([
            "[Safe](https://example.com/result)\n\n[Relative](/api/v1/runs)\n\n[Protocol-relative](//example.com)\n\n[Script](javascript:alert%281%29)\n\n[Data](data:text/html,hello)\n\n![Tracking image](https://example.com/pixel.png)\n\n![Private image](/api/v1/private)",
          ])
        }
      />
    )
    const link = await screen.findByRole("link", { name: "Safe" })
    expect(link).toHaveAttribute("href", "https://example.com/result")
    expect(link).toHaveAttribute("target", "_blank")
    expect(link).toHaveAttribute("rel", "noopener noreferrer")
    expect(screen.getAllByRole("link")).toHaveLength(1)
    expect(screen.getByText("Relative (link blocked)")).toBeVisible()
    expect(screen.getByText("Script (link blocked)")).toBeVisible()
    expect(
      screen.getByText("Tracking image (inline image blocked)")
    ).toBeVisible()
    expect(container.querySelector("img")).toBeNull()
  })
})

describe("JSON report", () => {
  it("renders an expandable typed tree preserving own special keys and exact strings", async () => {
    const { container } = render(
      <JsonReport
        title="Metrics"
        content={
          new Blob([
            '{"__proto__":{"nested":true},"constructor":"safe","values":[null,false,42,"<script>\\n"]}',
          ])
        }
      />
    )
    const summary = await screen.findByText("Metrics: Object (3)")
    expect(summary).toBeVisible()
    expect(screen.getByText('"constructor":')).toBeVisible()
    expect(screen.getByText('"safe"')).toBeVisible()
    const special = screen.getByText('"__proto__": Object (1)')
    expect(screen.queryByText('"nested":')).not.toBeInTheDocument()
    fireEvent.click(special)
    await screen.findByText('"nested":')
    expect(screen.getByText("true")).toBeVisible()
    fireEvent.click(screen.getByText('"values": Array (4)'))
    expect(await screen.findByText("null")).toBeVisible()
    expect(screen.getByText("false")).toBeVisible()
    expect(screen.getByText("42")).toBeVisible()
    expect(screen.getByText('"<script>\\n"')).toBeVisible()
    expect(container.querySelector("script")).toBeNull()
  })
  it.each(["null", "false", "42", '"root text"', "[]", "{}"])(
    "supports root JSON value %s",
    async (json) => {
      render(<JsonReport title="Root" content={new Blob([json])} />)
      await waitFor(() => expect(screen.queryByRole("status")).toBeNull())
      expect(screen.queryByRole("alert")).not.toBeInTheDocument()
      expect(screen.getByText(/Root:/)).toBeVisible()
    }
  )
  it("limits initial DOM work and makes every array item available on demand", async () => {
    render(
      <JsonReport
        title="Many"
        content={
          new Blob([JSON.stringify(Array.from({ length: 205 }, (_, i) => i))])
        }
      />
    )
    const more = await screen.findByRole("button", {
      name: "Show next 100 entries",
    })
    expect(screen.getByText("[99]:")).toBeVisible()
    expect(screen.queryByText("[100]:")).not.toBeInTheDocument()
    fireEvent.click(more)
    expect(screen.getByText("[199]:")).toBeVisible()
    fireEvent.click(screen.getByRole("button", { name: "Show next 5 entries" }))
    expect(screen.getByText("[204]:")).toBeVisible()
    expect(screen.queryByRole("button")).not.toBeInTheDocument()
  })
  it.each(['{"broken":', "<script>alert(1)</script>", ""])(
    "reports invalid JSON without breaking the surrounding UI",
    async (text) => {
      render(<JsonReport title="Invalid" content={new Blob([text])} />)
      expect(await screen.findByRole("alert")).toHaveTextContent(
        "Report content is not valid JSON."
      )
    }
  )
})

describe("raster image report", () => {
  const png = () =>
    new Blob([new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10])])
  beforeEach(() => {
    vi.stubGlobal(
      "URL",
      class extends URL {
        static createObjectURL = vi.fn(() => "blob:owned-report")
        static revokeObjectURL = vi.fn()
      }
    )
    vi.stubGlobal(
      "Image",
      class {
        src = ""
        async decode() {}
      }
    )
  })
  it("uses only owned, raster-typed Blob URLs and releases them on unmount", async () => {
    const view = render(
      <ImageReport content={png()} title="Chart screenshot" />
    )
    const image = await screen.findByRole("img", { name: "Chart screenshot" })
    expect(image).toHaveAttribute("src", "blob:owned-report")
    const create = vi.mocked(URL.createObjectURL)
    expect(create).toHaveBeenCalledOnce()
    expect(create.mock.calls[0]?.[0]).toBeInstanceOf(Blob)
    expect(create.mock.calls[0]?.[0]).toHaveProperty("type", "image/png")
    view.unmount()
    expect(URL.revokeObjectURL).toHaveBeenCalledWith("blob:owned-report")
  })
  it.each([
    ["<svg onload='alert(1)'></svg>", "image/svg+xml"],
    ["<html><script>alert(1)</script></html>", "image/png"],
    ["https://example.com/image.png", "image/png"],
    ["", "image/png"],
  ])(
    "rejects non-raster bytes regardless of claimed MIME %s",
    async (bytes, type) => {
      render(
        <ImageReport content={new Blob([bytes], { type })} title="Unsafe" />
      )
      expect(await screen.findByRole("alert")).toHaveTextContent(
        "Report content is not a supported raster image."
      )
      expect(URL.createObjectURL).not.toHaveBeenCalled()
      expect(screen.queryByRole("img")).not.toBeInTheDocument()
    }
  )
  it("surfaces image decode failure", async () => {
    render(<ImageReport content={png()} title="Broken PNG" />)
    fireEvent.error(await screen.findByRole("img"))
    expect(screen.getByRole("alert")).toHaveTextContent(
      "Report image could not be decoded."
    )
  })
  it("rejects corrupt raster data before displaying a broken image", async () => {
    vi.stubGlobal(
      "Image",
      class {
        src = ""
        async decode() {
          throw new Error("Corrupt image.")
        }
      }
    )
    render(<ImageReport content={png()} title="Corrupt raster" />)
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Report image could not be decoded."
    )
    expect(screen.queryByRole("img")).not.toBeInTheDocument()
  })
  it.each([
    [[255, 216, 255, 224], "image/jpeg"],
    [[71, 73, 70, 56, 57, 97], "image/gif"],
    [[82, 73, 70, 70, 0, 0, 0, 0, 87, 69, 66, 80], "image/webp"],
    [[66, 77], "image/bmp"],
    [[0, 0, 1, 0], "image/x-icon"],
    [[0, 0, 0, 32, 102, 116, 121, 112, 97, 118, 105, 102], "image/avif"],
  ] as const)("recognizes raster signature %j as %s", async (header, mime) => {
    render(
      <ImageReport
        content={new Blob([new Uint8Array(header)])}
        title="Raster"
      />
    )
    await screen.findByRole("img")
    expect(vi.mocked(URL.createObjectURL).mock.calls[0]?.[0]).toHaveProperty(
      "type",
      mime
    )
  })
  it("releases the prior Blob URL when the report changes", async () => {
    vi.mocked(URL.createObjectURL)
      .mockReturnValueOnce("blob:first")
      .mockReturnValueOnce("blob:second")
    const view = render(<ImageReport content={png()} title="First" />)
    await screen.findByRole("img", { name: "First" })
    view.rerender(<ImageReport content={png()} title="Second" />)
    await waitFor(() =>
      expect(screen.getByRole("img", { name: "Second" })).toHaveAttribute(
        "src",
        "blob:second"
      )
    )
    expect(URL.revokeObjectURL).toHaveBeenCalledWith("blob:first")
    view.unmount()
    expect(URL.revokeObjectURL).toHaveBeenCalledWith("blob:second")
  })
  it("surfaces Blob URL creation failures instead of leaving a loading state", async () => {
    vi.mocked(URL.createObjectURL).mockImplementation(() => {
      throw new Error("Blob URLs unavailable.")
    })
    render(<ImageReport content={png()} title="Unavailable" />)
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Blob URLs unavailable."
    )
  })
  it("does not leak an Object URL through StrictMode effect replay", async () => {
    const view = render(
      <StrictMode>
        <ImageReport content={png()} title="Strict" />
      </StrictMode>
    )
    await screen.findByRole("img")
    view.unmount()
    expect(URL.createObjectURL).toHaveBeenCalledOnce()
    expect(URL.revokeObjectURL).toHaveBeenCalledOnce()
  })
})
