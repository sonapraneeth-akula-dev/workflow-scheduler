import { BlobText } from "../BlobText"
import { Button } from "@/components/ui/button"
import { ErrorNotice } from "@/components/ErrorNotice"
import { useProgressiveContent } from "../useProgressiveContent"
import type { ReportRendererProps } from "../types"

export default function TextReport({ content, title }: ReportRendererProps) {
  if (content.size > 5_000_000)
    return <ProgressiveText content={content} title={title} />
  return (
    <BlobText content={content}>
      {(text) => (
        <pre
          aria-label={title}
          role="region"
          tabIndex={0}
          className="max-h-[70vh] overflow-auto rounded border p-4 text-sm break-words whitespace-pre-wrap"
        >
          {text}
        </pre>
      )}
    </BlobText>
  )
}

function ProgressiveText({ content, title }: ReportRendererProps) {
  const state = useProgressiveContent(content)
  if (state.error) return <ErrorNotice error={state.error} />
  if (state.text === undefined)
    return <p role="status">Reading report content...</p>
  return (
    <section className="min-w-0 space-y-3" aria-label={`${title} paged text`}>
      <div className="flex flex-wrap items-center gap-3">
        <Button
          variant="outline"
          disabled={state.pending || state.page === 0}
          onClick={state.previous}
        >
          Previous text page
        </Button>
        <p role="status" aria-live="polite">
          Page {(state.page ?? 0) + 1};{" "}
          {state.bytesRead?.toLocaleString("en-US")} of{" "}
          {content.size.toLocaleString("en-US")} bytes read.
        </p>
        <Button
          variant="outline"
          disabled={
            state.pending ||
            (state.complete && state.page === (state.pages?.length ?? 0) - 1)
          }
          onClick={state.next}
        >
          Next text page
        </Button>
      </div>
      <pre
        key={state.page}
        role="region"
        aria-label={title}
        tabIndex={0}
        className="max-h-[70vh] overflow-auto rounded border p-4 text-sm break-words whitespace-pre-wrap"
      >
        {state.text}
      </pre>
    </section>
  )
}
