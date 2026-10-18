import { useEffect, useState } from "react"
import { ErrorNotice } from "@/components/ErrorNotice"
import type { ReportRendererProps } from "./types"

type DownloadState =
  | { content: Blob; url: string; error?: never }
  | { content: Blob; url?: never; error: unknown }

export function DownloadFallback({ content, title }: ReportRendererProps) {
  const [state, setState] = useState<DownloadState | null>(null)
  useEffect(() => {
    let url: string | undefined
    let result: DownloadState
    try {
      url = URL.createObjectURL(
        new Blob([content], { type: "application/octet-stream" })
      )
      result = { content, url }
    } catch (error) {
      result = { content, error }
    }
    // Owned browser resources must be allocated after commit, not during render.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setState(result)
    return () => {
      if (url) URL.revokeObjectURL(url)
    }
  }, [content])

  if (state?.content !== content)
    return <p role="status">Preparing report download...</p>
  if ("error" in state) return <ErrorNotice error={state.error} />
  const filename =
    title
      .replace(/[<>:"/\\|?*\p{Cc}]/gu, "_")
      .replace(/[. ]+$/u, "")
      .slice(0, 200) || "report"
  return (
    <div className="space-y-2 break-words">
      <p>No inline renderer is registered for this report kind.</p>
      <a
        className="text-primary underline"
        href={state.url}
        download={filename}
      >
        Download {title}
      </a>
      <p>{content.size.toLocaleString("en-US")} bytes</p>
    </div>
  )
}
