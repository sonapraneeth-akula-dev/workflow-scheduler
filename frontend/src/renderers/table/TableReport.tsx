import { useEffect, useRef, useState } from "react"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
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
  const [loadingPage, setLoadingPage] = useState(false)
  const currentWorker = useRef<Worker | null>(null)
  useEffect(() => {
    let active = true
    let worker: Worker | undefined
    try {
      worker = new Worker(new URL("./table.worker.ts", import.meta.url), {
        type: "module",
      })
      currentWorker.current = worker
      worker.onmessage = (event: MessageEvent<TableResult>) => {
        if (active) {
          setState({ ...event.data, content, format })
          setLoadingPage(false)
        }
        if ("error" in event.data || !event.data.pagination) worker?.terminate()
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
      if (currentWorker.current === worker) currentWorker.current = null
    }
  }, [content, format])
  if (state?.content !== content || state.format !== format)
    return <p role="status">Parsing table in a worker...</p>
  if ("error" in state) return <ErrorNotice error={state.error} />
  const pagination = state.pagination
  const requestPage = (page: number) => {
    try {
      if (!currentWorker.current)
        throw new Error("Table parser worker is unavailable.")
      setLoadingPage(true)
      currentWorker.current.postMessage({ page } satisfies TableRequest)
    } catch (error) {
      setState({ content, format, error: String(error) })
    }
  }
  return (
    <>
      {pagination ? (
        <div className="flex flex-wrap items-center gap-3">
          <Button
            variant="outline"
            disabled={loadingPage || pagination.page === 0}
            onClick={() => requestPage(pagination.page - 1)}
          >
            Previous table page
          </Button>
          <p role="status" aria-live="polite">
            Page {pagination.page + 1};{" "}
            {pagination.parsedRows.toLocaleString("en-US")} rows parsed.
            {pagination.complete
              ? " Report parsing complete."
              : " More rows available; later pages are not yet validated."}{" "}
            Sorting and filtering apply to this page.
          </p>
          <Button
            variant="outline"
            disabled={
              loadingPage ||
              (pagination.complete &&
                pagination.page + 1 === pagination.pageCount)
            }
            onClick={() => requestPage(pagination.page + 1)}
          >
            {loadingPage ? "Loading table page..." : "Next table page"}
          </Button>
        </div>
      ) : null}
      <VirtualTable
        key={`${title}:${pagination?.page ?? 0}`}
        {...state.data}
        title={title}
      />
    </>
  )
}

export default function TableReport(props: ReportRendererProps) {
  return <TableContent {...props} format="auto" />
}
