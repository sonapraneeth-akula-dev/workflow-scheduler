import { useEffect, useState } from "react"
import { ErrorNotice } from "@/components/ErrorNotice"
import type { ReportRendererProps } from "../types"

function rasterType(header: Uint8Array): string | null {
  const starts = (...bytes: number[]) =>
    bytes.every((value, index) => header[index] === value)
  const ascii = (start: number, length: number) =>
    String.fromCharCode(...header.slice(start, start + length))
  if (starts(137, 80, 78, 71, 13, 10, 26, 10)) return "image/png"
  if (starts(255, 216, 255)) return "image/jpeg"
  if (["GIF87a", "GIF89a"].includes(ascii(0, 6))) return "image/gif"
  if (ascii(0, 4) === "RIFF" && ascii(8, 4) === "WEBP") return "image/webp"
  if (starts(66, 77)) return "image/bmp"
  if (starts(0, 0, 1, 0)) return "image/x-icon"
  if (
    ascii(4, 4) === "ftyp" &&
    [8, 16, 20, 24, 28].some((offset) =>
      ["avif", "avis"].includes(ascii(offset, 4))
    )
  )
    return "image/avif"
  return null
}

type ImageState =
  | { content: Blob; url: string; error?: never }
  | { content: Blob; url?: never; error: Error }

export default function ImageReport({ content, title }: ReportRendererProps) {
  const [state, setState] = useState<ImageState | null>(null)
  useEffect(() => {
    let active = true
    let objectUrl: string | undefined
    async function readImage() {
      try {
        const buffer = await content.slice(0, 32).arrayBuffer()
        if (!active) return
        const type = rasterType(new Uint8Array(buffer))
        if (!type)
          throw new Error("Report content is not a supported raster image.")
        objectUrl = URL.createObjectURL(new Blob([content], { type }))
        const image = new Image()
        image.src = objectUrl
        try {
          await image.decode()
        } catch {
          throw new Error("Report image could not be decoded.")
        }
        if (active) setState({ content, url: objectUrl })
      } catch (error) {
        if (active)
          setState({
            content,
            error:
              error instanceof Error
                ? error
                : new Error("Report image could not be read."),
          })
      }
    }
    void readImage()
    return () => {
      active = false
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    }
  }, [content])

  if (state?.content !== content)
    return <p role="status">Reading report image...</p>
  if ("error" in state) return <ErrorNotice error={state.error} />
  return (
    <img
      src={state.url}
      alt={title}
      className="h-auto max-w-full rounded"
      onError={() =>
        setState({
          content,
          error: new Error("Report image could not be decoded."),
        })
      }
    />
  )
}
