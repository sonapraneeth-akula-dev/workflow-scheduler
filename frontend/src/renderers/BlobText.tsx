import { useEffect, useState, type ReactNode } from "react"
import { ErrorNotice } from "@/components/ErrorNotice"
import type { ReportRendererProps } from "./types"

type TextState =
  | { content: Blob; text: string; error?: never }
  | { content: Blob; text?: never; error: unknown }

export function BlobText({
  content,
  children,
}: Pick<ReportRendererProps, "content"> & {
  children: (text: string) => ReactNode
}) {
  const [state, setState] = useState<TextState | null>(null)
  useEffect(() => {
    let active = true
    void content.arrayBuffer().then(
      (buffer) => {
        if (!active) return
        try {
          const text = new TextDecoder("utf-8", { fatal: true }).decode(buffer)
          setState({ content, text })
        } catch {
          setState({
            content,
            error: new Error("Report content is not valid UTF-8 text."),
          })
        }
      },
      () => {
        if (active)
          setState({
            content,
            error: new Error("Report content could not be read."),
          })
      }
    )
    return () => {
      active = false
    }
  }, [content])

  if (state?.content !== content)
    return <p role="status">Reading report content...</p>
  if ("error" in state) return <ErrorNotice error={state.error} />
  return children(state.text)
}
