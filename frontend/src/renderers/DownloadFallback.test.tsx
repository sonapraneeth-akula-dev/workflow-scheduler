import { StrictMode } from "react"
import { render, screen } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { DownloadFallback } from "./DownloadFallback"

beforeEach(() => {
  vi.stubGlobal(
    "URL",
    class extends URL {
      static createObjectURL = vi.fn(() => "blob:download")
      static revokeObjectURL = vi.fn()
    }
  )
})
afterEach(() => vi.unstubAllGlobals())

describe("unknown report kind download", () => {
  it("shows the exact title, byte size and an owned binary download link", () => {
    const view = render(
      <DownloadFallback
        content={new Blob(["payload"])}
        title="Unfamiliar kind"
      />
    )
    const link = screen.getByRole("link", { name: "Download Unfamiliar kind" })
    expect(link).toHaveAttribute("href", "blob:download")
    expect(link).toHaveAttribute("download", "Unfamiliar kind")
    expect(screen.getByText("7 bytes")).toBeVisible()
    expect(vi.mocked(URL.createObjectURL).mock.calls[0]?.[0]).toHaveProperty(
      "type",
      "application/octet-stream"
    )
    view.unmount()
    expect(URL.revokeObjectURL).toHaveBeenCalledWith("blob:download")
  })
  it("retains visible untrusted titles as text while sanitizing the filename", () => {
    const title = "<script>report</script>../unsafe:name. "
    const { container } = render(
      <DownloadFallback content={new Blob()} title={title} />
    )
    const link = screen.getByRole("link")
    expect(link.textContent).toBe(`Download ${title}`)
    expect(link.getAttribute("download")).not.toMatch(/[<>:"/\\|?*]/)
    expect(link.getAttribute("download")).not.toMatch(/[. ]$/)
    expect(container.querySelector("script")).toBeNull()
    expect(screen.getByText("0 bytes")).toBeVisible()
  })
  it("replaces URLs on Blob changes and does not reallocate on title changes", () => {
    const first = new Blob(["first"])
    const second = new Blob(["second"])
    const view = render(<DownloadFallback content={first} title="First" />)
    view.rerender(<DownloadFallback content={first} title="Renamed" />)
    expect(URL.createObjectURL).toHaveBeenCalledOnce()
    expect(screen.getByRole("link")).toHaveAttribute("download", "Renamed")
    view.rerender(<DownloadFallback content={second} title="Second" />)
    expect(URL.createObjectURL).toHaveBeenCalledTimes(2)
    expect(URL.revokeObjectURL).toHaveBeenCalledTimes(1)
    expect(screen.getByText("6 bytes")).toBeVisible()
    view.unmount()
    expect(URL.revokeObjectURL).toHaveBeenCalledTimes(2)
  })
  it("balances every resource under StrictMode effect replay", () => {
    const view = render(
      <StrictMode>
        <DownloadFallback content={new Blob(["data"])} title="Strict" />
      </StrictMode>
    )
    view.unmount()
    expect(URL.createObjectURL).toHaveBeenCalledTimes(2)
    expect(URL.revokeObjectURL).toHaveBeenCalledTimes(2)
  })
  it("surfaces browser resource allocation failures", () => {
    vi.mocked(URL.createObjectURL).mockImplementation(() => {
      throw new Error("Downloads unavailable.")
    })
    render(<DownloadFallback content={new Blob()} title="Unavailable" />)
    expect(screen.getByRole("alert")).toHaveTextContent(
      "Downloads unavailable."
    )
    expect(screen.queryByRole("link")).not.toBeInTheDocument()
  })
})
