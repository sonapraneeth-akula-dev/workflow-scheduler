import { render, screen, waitFor } from "@testing-library/react"
import { describe, expect, it } from "vitest"
import HtmlReport from "./HtmlReport"

describe("sandboxed HTML and SVG report frame", () => {
  it("places CSP before untrusted bytes and grants scripts without same-origin, forms or navigation", async () => {
    const payload =
      '</head><body><script>parent.document.body.textContent="escaped"</script><h1>Untrusted report</h1>'
    render(
      <HtmlReport
        title="Committed title"
        content={new Blob([payload], { type: "text/html" })}
      />
    )
    const frame = await screen.findByTitle("Committed title (sandboxed report)")
    expect(frame).toHaveAttribute("sandbox", "allow-scripts")
    expect(frame).toHaveAttribute("referrerpolicy", "no-referrer")
    const document = frame.getAttribute("srcdoc")!
    expect(document.indexOf("Content-Security-Policy")).toBeLessThan(
      document.indexOf(payload)
    )
    for (const directive of [
      "default-src 'none'",
      "connect-src 'none'",
      "form-action 'none'",
      "base-uri 'none'",
    ])
      expect(document).toContain(directive)
    expect(document).toContain(payload)
    expect(frame).not.toHaveAttribute("src")
    expect(
      screen.queryByRole("heading", { name: "Untrusted report" })
    ).toBeNull()
  })
  it("embeds SVG as untrusted markup in the same isolated HTML document", async () => {
    const svg =
      '<svg xmlns="http://www.w3.org/2000/svg"><text>Result</text></svg>'
    render(
      <HtmlReport
        title="SVG result"
        content={new Blob([svg], { type: "image/svg+xml" })}
      />
    )
    const frame = await screen.findByTitle("SVG result (sandboxed report)")
    expect(frame.getAttribute("srcdoc")).toContain(svg)
    expect(frame).toHaveAttribute("sandbox", "allow-scripts")
    expect(screen.queryByText("Result")).toBeNull()
  })
  it("replaces the isolated document when content or committed title changes", async () => {
    const first = new Blob(["<p>first</p>"])
    const { rerender } = render(<HtmlReport title="First" content={first} />)
    const frame = await screen.findByTitle("First (sandboxed report)")
    expect(frame.getAttribute("srcdoc")).toContain("<p>first</p>")
    rerender(
      <HtmlReport title="Second" content={new Blob(["<p>second</p>"])} />
    )
    await waitFor(() =>
      expect(
        screen.getByTitle("Second (sandboxed report)").getAttribute("srcdoc")
      ).toContain("<p>second</p>")
    )
    expect(screen.queryByTitle("First (sandboxed report)")).toBeNull()
  })
  it("rejects invalid UTF-8 instead of opening an executable replacement document", async () => {
    render(
      <HtmlReport title="Broken" content={new Blob([new Uint8Array([255])])} />
    )
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Report content is not valid UTF-8 text."
    )
    expect(screen.queryByTitle("Broken (sandboxed report)")).toBeNull()
  })
})
