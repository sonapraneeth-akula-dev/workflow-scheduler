import { Blob as NodeBlob } from "node:buffer"
import {
  act,
  fireEvent,
  render,
  renderHook,
  screen,
  waitFor,
} from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import TextReport from "./text/TextReport"
import { useProgressiveContent } from "./useProgressiveContent"

describe("bounded progressive UTF-8 text pages", () => {
  beforeEach(() => vi.stubGlobal("Blob", NodeBlob))
  afterEach(() => vi.unstubAllGlobals())

  it("decodes split multibyte characters exactly and navigates cached pages without rereading", async () => {
    const content = new Blob(["a".repeat(65_535), "\u03bb", "second page"])
    const slice = vi.spyOn(content, "slice")
    const { result } = renderHook(() => useProgressiveContent(content))
    await waitFor(() => expect(result.current.text).toHaveLength(65_535))
    expect(result.current).toMatchObject({
      bytesRead: 65_536,
      complete: false,
      page: 0,
    })
    act(() => {
      result.current.next()
      result.current.next()
    })
    await waitFor(() => expect(result.current.text).toBe("\u03bbsecond page"))
    expect(result.current.complete).toBe(true)
    expect(slice).toHaveBeenCalledTimes(2)
    act(() => result.current.previous())
    expect(result.current.text).toHaveLength(65_535)
    act(() => result.current.next())
    expect(result.current.text).toBe("\u03bbsecond page")
    expect(slice).toHaveBeenCalledTimes(2)
  })

  it("clears prior text and exposes fatal errors in later pages", async () => {
    const content = new Blob(["a".repeat(65_536), Uint8Array.of(255)])
    const { result } = renderHook(() => useProgressiveContent(content))
    await waitFor(() => expect(result.current.text).toHaveLength(65_536))
    act(() => result.current.next())
    await waitFor(() => expect(result.current.error).toBeInstanceOf(Error))
    expect(result.current.text).toBeUndefined()
    expect(result.current.error).toMatchObject({
      message: "Report content is not valid UTF-8 text.",
    })
  })

  it("ignores late reads after replacement and after unmount", async () => {
    let finish: ((value: ArrayBuffer) => void) | undefined
    const old = new Blob(["old"])
    vi.spyOn(old, "slice").mockReturnValue(
      Object.assign(new Blob(), {
        arrayBuffer: () =>
          new Promise<ArrayBuffer>((resolve) => {
            finish = resolve
          }),
      })
    )
    const { result, rerender, unmount } = renderHook(
      ({ content }) => useProgressiveContent(content),
      { initialProps: { content: old } }
    )
    rerender({ content: new Blob(["new"]) })
    await waitFor(() => expect(result.current.text).toBe("new"))
    await act(async () => finish?.(new TextEncoder().encode("old").buffer))
    expect(result.current.text).toBe("new")
    unmount()
  })

  it("surfaces slice failures rather than a success-shaped empty page", async () => {
    const content = new Blob(["x"])
    vi.spyOn(content, "slice").mockImplementation(() => {
      throw new Error("Read failed.")
    })
    const { result } = renderHook(() => useProgressiveContent(content))
    await waitFor(() =>
      expect(result.current.error).toMatchObject({ message: "Read failed." })
    )
    expect(result.current.text).toBeUndefined()
  })

  it("renders only one accessible text page above the exact decimal five-MB boundary", async () => {
    const content = new Blob(["a".repeat(5_000_001)])
    render(<TextReport content={content} title="Large text" />)
    const text = await screen.findByRole("region", {
      name: "Large text",
    })
    expect(text.textContent).toHaveLength(65_536)
    expect(screen.getByRole("status")).toHaveTextContent(
      "65,536 of 5,000,001 bytes read"
    )
    expect(
      screen.getByRole("button", { name: "Previous text page" })
    ).toBeDisabled()
    fireEvent.click(screen.getByRole("button", { name: "Next text page" }))
    await waitFor(() =>
      expect(screen.getByRole("status")).toHaveTextContent("Page 2")
    )
    expect(text.textContent).toHaveLength(65_536)
  })
})
