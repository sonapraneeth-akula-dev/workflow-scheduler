import { useEffect, useRef, useState } from "react"
import {
  getDocument,
  GlobalWorkerOptions,
  version,
  type PDFDocumentLoadingTask,
  type PDFDocumentProxy,
  type PDFPageProxy,
  type RenderTask,
} from "pdfjs-dist"
import workerUrl from "pdfjs-dist/build/pdf.worker.min.mjs?url"
import { Button } from "@/components/ui/button"
import { ErrorNotice } from "@/components/ErrorNotice"
import type { ReportRendererProps } from "../types"

GlobalWorkerOptions.workerSrc = workerUrl
const assets = `${import.meta.env.BASE_URL}assets/pdfjs/${version}/`

type DocumentState =
  | { content: Blob; document: PDFDocumentProxy; error?: never }
  | { content: Blob; document?: never; error: unknown }

async function readPageText(page: PDFPageProxy) {
  const reader: ReadableStreamDefaultReader<
    Awaited<ReturnType<PDFPageProxy["getTextContent"]>>
  > = page.streamTextContent().getReader()
  const parts: string[] = []
  try {
    // PDF.js getTextContent requires stream async iteration, absent in WebKit.
    for (;;) {
      const { done, value } = await reader.read()
      if (done) return parts.join("\n")
      parts.push(...value.items.map((item) => ("str" in item ? item.str : "")))
    }
  } finally {
    reader.releaseLock()
  }
}

function Page({
  document,
  number,
  title,
}: {
  document: PDFDocumentProxy
  number: number
  title: string
}) {
  const canvas = useRef<HTMLCanvasElement>(null)
  const [text, setText] = useState<string | null>(null)
  const [error, setError] = useState<unknown>(null)
  useEffect(() => {
    let active = true
    let page: PDFPageProxy | undefined
    let render: RenderTask | undefined
    void (async () => {
      try {
        page = await document.getPage(number)
        if (!active) {
          page.cleanup()
          return
        }
        const base = page.getViewport({ scale: 1 })
        if (
          !Number.isFinite(base.width * base.height) ||
          base.width <= 0 ||
          base.height <= 0
        )
          throw new Error("PDF page dimensions are invalid.")
        const target = canvas.current!
        const scale = Math.min(
          800 / base.width,
          8192 / base.width,
          8192 / base.height,
          Math.sqrt(4_000_000 / (base.width * base.height))
        )
        const viewport = page.getViewport({ scale })
        target.width = Math.max(1, Math.floor(viewport.width))
        target.height = Math.max(1, Math.floor(viewport.height))
        render = page.render({ canvas: target, viewport })
        const [, content] = await Promise.all([
          render.promise,
          readPageText(page),
        ])
        if (active) setText(content)
      } catch (failure) {
        if (active) setError(failure)
      }
    })()
    return () => {
      active = false
      render?.cancel()
      page?.cleanup()
    }
  }, [document, number])
  return (
    <section
      className="min-w-0 space-y-3"
      aria-label={`${title} page ${number}`}
    >
      {error ? <ErrorNotice error={error} /> : null}
      {text === null && !error ? (
        <p role="status">Rendering PDF page...</p>
      ) : null}
      <canvas
        ref={canvas}
        role="img"
        aria-label={`${title}: page ${number} of ${document.numPages}`}
        className="h-auto max-w-full border bg-white"
        hidden={text === null || !!error}
      />
      {text !== null && !error ? (
        <details>
          <summary className="cursor-pointer">PDF page text</summary>
          <pre className="break-words whitespace-pre-wrap">
            {text || "This page contains no extractable text."}
          </pre>
        </details>
      ) : null}
    </section>
  )
}

function Document({
  document,
  title,
}: {
  document: PDFDocumentProxy
  title: string
}) {
  const [page, setPage] = useState(1)
  return (
    <section className="min-w-0 space-y-3" aria-label={`${title} PDF`}>
      <div className="flex flex-wrap items-center gap-3">
        <Button
          variant="outline"
          disabled={page === 1}
          onClick={() => setPage((value) => value - 1)}
        >
          Previous PDF page
        </Button>
        <p aria-live="polite">
          Page {page} of {document.numPages}
        </p>
        <Button
          variant="outline"
          disabled={page === document.numPages}
          onClick={() => setPage((value) => value + 1)}
        >
          Next PDF page
        </Button>
      </div>
      <Page key={page} document={document} number={page} title={title} />
    </section>
  )
}

export default function PdfReport({ content, title }: ReportRendererProps) {
  const [state, setState] = useState<DocumentState | null>(null)
  useEffect(() => {
    let active = true
    let task: PDFDocumentLoadingTask | undefined
    void content.arrayBuffer().then(
      async (data) => {
        if (!active) return
        try {
          task = getDocument({
            data: new Uint8Array(data),
            stopAtErrors: true,
            enableXfa: false,
            canvasMaxAreaInBytes: 16_000_000,
            cMapUrl: `${assets}cmaps/`,
            standardFontDataUrl: `${assets}standard_fonts/`,
            wasmUrl: `${assets}wasm/`,
            iccUrl: `${assets}iccs/`,
          })
          const document = await task.promise
          if (active) setState({ content, document })
        } catch (error) {
          if (active) setState({ content, error })
        }
      },
      () => {
        if (active)
          setState({
            content,
            error: new Error("PDF content could not be read."),
          })
      }
    )
    return () => {
      active = false
      void task
        ?.destroy()
        .catch((error: unknown) =>
          console.error("PDF renderer cleanup failed.", error)
        )
    }
  }, [content])
  if (state?.content !== content)
    return <p role="status">Loading PDF document...</p>
  if ("error" in state) return <ErrorNotice error={state.error} />
  return (
    <Document
      key={state.document.fingerprints.join("/")}
      document={state.document}
      title={title}
    />
  )
}
