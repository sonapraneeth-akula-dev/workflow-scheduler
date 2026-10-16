import { BlobText } from "../BlobText"
import type { ReportRendererProps } from "../types"

export default function TextReport({ content, title }: ReportRendererProps) {
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
