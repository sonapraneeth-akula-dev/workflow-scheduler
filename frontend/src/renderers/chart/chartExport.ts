import type uPlot from "uplot"

const maximumSide = 8192
const maximumPixels = 16_000_000

function wrapText(
  context: CanvasRenderingContext2D,
  text: string,
  width: number
) {
  const lines: string[] = []
  let line = ""
  for (const word of text.trim().split(/\s+/)) {
    if (line && context.measureText(`${line} ${word}`).width > width) {
      lines.push(line)
      line = ""
    }
    for (const character of `${line ? " " : ""}${word}`) {
      if (line && context.measureText(line + character).width > width) {
        lines.push(line)
        line = ""
      }
      line += character
    }
  }
  if (line) lines.push(line)
  return lines
}

export async function createChartPng(
  plot: Pick<uPlot, "root" | "width" | "height"> & {
    ctx: Pick<CanvasRenderingContext2D, "canvas">
  },
  title: string,
  labels: readonly string[]
): Promise<Blob> {
  const source = plot.ctx.canvas
  if (plot.width <= 0 || plot.height <= 0 || !source.width || !source.height)
    throw new Error("The chart must be visible before exporting a PNG.")
  const canvas = document.createElement("canvas")
  const context = canvas.getContext("2d")
  if (!context) throw new Error("PNG export requires a 2D canvas.")
  const style = getComputedStyle(plot.root)
  const font = style.fontFamily || "sans-serif"
  const padding = 16
  const width = Math.max(200, plot.width)
  context.font = `600 16px ${font}`
  const titleLines = wrapText(context, title, width - padding * 2)
  context.font = `13px ${font}`
  const legend = labels.flatMap((label, index) =>
    wrapText(context, label, width - padding * 2 - 20).map((text, row) => ({
      text,
      index,
      first: row === 0,
    }))
  )
  const chartTop = padding + titleLines.length * 22 + 12
  const legendTop = chartTop + plot.height + 12
  const height = legendTop + legend.length * 20 + padding
  const scale = Math.min(
    2,
    maximumSide / width,
    maximumSide / height,
    Math.sqrt(maximumPixels / (width * height))
  )
  canvas.width = Math.floor(width * scale)
  canvas.height = Math.floor(height * scale)
  context.scale(scale, scale)
  context.fillStyle = style.getPropertyValue("--card").trim() || "#ffffff"
  context.fillRect(0, 0, width, height)
  context.textBaseline = "top"
  context.fillStyle =
    style.getPropertyValue("--foreground").trim() || style.color
  context.font = `600 16px ${font}`
  titleLines.forEach((line, index) =>
    context.fillText(line, padding, padding + index * 22)
  )
  context.drawImage(source, 0, chartTop, plot.width, plot.height)
  context.font = `13px ${font}`
  legend.forEach(({ text, index, first }, row) => {
    const top = legendTop + row * 20
    if (first) {
      context.fillStyle =
        style.getPropertyValue(`--chart-${(index % 5) + 1}`).trim() ||
        style.color
      context.fillRect(padding, top + 3, 10, 10)
    }
    context.fillStyle =
      style.getPropertyValue("--foreground").trim() || style.color
    context.fillText(text, padding + 20, top)
  })
  return new Promise((resolve, reject) => {
    canvas.toBlob(
      (blob) =>
        blob ? resolve(blob) : reject(new Error("PNG encoding failed.")),
      "image/png"
    )
  })
}
