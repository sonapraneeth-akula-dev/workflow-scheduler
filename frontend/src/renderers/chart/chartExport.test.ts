import { afterEach, describe, expect, it, vi } from "vitest"
import { createChartPng } from "./chartExport"

function fixture(width = 640, height = 360) {
  const canvas = document.createElement("canvas")
  const context = {
    canvas,
    font: "",
    fillStyle: "",
    textBaseline: "alphabetic",
    measureText: vi.fn(
      (text: string) => ({ width: text.length * 7 }) as TextMetrics
    ),
    fillRect: vi.fn<CanvasRenderingContext2D["fillRect"]>(),
    fillText: vi.fn<CanvasRenderingContext2D["fillText"]>(),
    drawImage:
      vi.fn<(image: CanvasImageSource, ...coordinates: number[]) => void>(),
    scale: vi.fn<CanvasRenderingContext2D["scale"]>(),
  } satisfies Partial<CanvasRenderingContext2D>
  const partial: Partial<CanvasRenderingContext2D> = context
  const images: HTMLCanvasElement[] = []
  const getContext = vi
    .spyOn(HTMLCanvasElement.prototype, "getContext")
    .mockReturnValue(partial as CanvasRenderingContext2D)
  const toBlob = vi
    .spyOn(HTMLCanvasElement.prototype, "toBlob")
    .mockImplementation(function (this: HTMLCanvasElement, callback) {
      images.push(this)
      callback(new Blob(["encoded"], { type: "image/png" }))
    })
  return {
    plot: {
      root: document.createElement("div"),
      width,
      height,
      ctx: { canvas },
    },
    context,
    getContext,
    toBlob,
    images,
  }
}

afterEach(() => vi.restoreAllMocks())

describe("native chart PNG export", () => {
  it("composes the title, complete chart canvas, opaque background and series legend at 2x resolution", async () => {
    const { plot, context, toBlob, images } = fixture()
    const blob = await createChartPng(plot, "Learning curve", [
      "Training",
      "Validation",
    ])
    expect(blob.type).toBe("image/png")
    expect(context.scale).toHaveBeenCalledWith(2, 2)
    expect(context.drawImage).toHaveBeenCalledWith(
      plot.ctx.canvas,
      0,
      expect.any(Number),
      640,
      360
    )
    expect(context.fillRect).toHaveBeenCalledWith(0, 0, 640, expect.any(Number))
    expect(context.fillText.mock.calls.map(([text]) => text)).toEqual([
      "Learning curve",
      "Training",
      "Validation",
    ])
    expect(toBlob).toHaveBeenCalledWith(expect.any(Function), "image/png")
    const image = images[0]!
    expect(image.width).toBe(1280)
    expect(image.height).toBeGreaterThan(720)
  })

  it("wraps long titles and legends instead of cropping their context", async () => {
    const { plot, context } = fixture(240)
    await createChartPng(
      plot,
      "A long chart title with a complete explanation",
      ["An unbroken label " + "x".repeat(80)]
    )
    const text = context.fillText.mock.calls.map(([value]) => value).join("")
    expect(text.replace(/\s/g, "")).toBe(
      (
        "A long chart title with a complete explanationAn unbroken label " +
        "x".repeat(80)
      ).replace(/\s/g, "")
    )
    expect(context.fillText.mock.calls.length).toBeGreaterThan(2)
  })

  it("caps wide exports at the browser-safe side and pixel budget", async () => {
    const { plot, images } = fixture(20_000, 2000)
    await createChartPng(plot, "Wide chart", ["Value"])
    const image = images[0]!
    expect(image.width).toBeLessThanOrEqual(8192)
    expect(image.height).toBeLessThanOrEqual(8192)
    expect(image.width * image.height).toBeLessThanOrEqual(16_000_000)
  })

  it("rejects invisible plots and canvas allocation failures explicitly", async () => {
    const { plot, getContext } = fixture(0)
    await expect(createChartPng(plot, "Hidden", ["Value"])).rejects.toThrow(
      "must be visible"
    )
    getContext.mockReturnValue(null)
    await expect(
      createChartPng({ ...plot, width: 640 }, "Unavailable", ["Value"])
    ).rejects.toThrow("requires a 2D canvas")
  })

  it("rejects failed encoding instead of downloading a null or empty success", async () => {
    const { plot, toBlob } = fixture()
    toBlob.mockImplementation((callback) => callback(null))
    await expect(createChartPng(plot, "Failed", ["Value"])).rejects.toThrow(
      "PNG encoding failed"
    )
  })
})
