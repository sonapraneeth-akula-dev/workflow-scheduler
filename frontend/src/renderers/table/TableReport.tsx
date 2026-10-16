import { useEffect, useState } from "react"
import { ErrorNotice } from "@/components/ErrorNotice"
import type { ReportRendererProps } from "../types"
import type { TableRequest, TableResult } from "./types"
import { VirtualTable } from "./VirtualTable"

type State = TableResult & { content: Blob; format: "auto" | "csv" }

export function TableContent({
  content,
  title,
  format,
}: ReportRendererProps & { format: "auto" | "csv" }) {
  const [state, setState] = useState<State | null>(null)
  useEffect(() => {
    let active = true
    let worker: Worker | undefined
    try {
      worker = new Worker(new URL("./table.worker.ts", import.meta.url), {
        type: "module",
      })
      worker.onmessage = (event: MessageEvent<TableResult>) => {
        if (active) setState({ ...event.data, content, format })
        worker?.terminate()
      }
      worker.onerror = (event) => {
        event.preventDefault()
        if (active)
          setState({ content, format, error: "Table parser worker failed." })
        worker?.terminate()
      }
      worker.postMessage({ content, format } satisfies TableRequest)
    } catch (error) {
      // Native worker allocation and structured cloning happen after commit.
      // eslint-disable-next-line react-hooks/set-state-in-effect
      setState({ content, format, error: String(error) })
      worker?.terminate()
    }
    return () => {
      active = false
      worker?.terminate()
    }
  }, [content, format])
  if (state?.content !== content || state.format !== format)
    return <p role="status">Parsing table in a worker...</p>
  if ("error" in state) return <ErrorNotice error={state.error} />
  return <VirtualTable key={title} {...state.data} title={title} />
}

export default function TableReport(props: ReportRendererProps) {
  return <TableContent {...props} format="auto" />
}
