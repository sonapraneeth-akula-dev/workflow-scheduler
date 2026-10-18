import { useState } from "react"
import { Button } from "@/components/ui/button"
import { ErrorNotice } from "@/components/ErrorNotice"
import { BlobText } from "../BlobText"
import type { ReportRendererProps } from "../types"

function JsonNode({
  value,
  label,
  root = false,
}: {
  value: unknown
  label: string
  root?: boolean
}) {
  const [open, setOpen] = useState(root)
  const [visible, setVisible] = useState(100)
  if (value === null || typeof value !== "object") {
    return (
      <div className="break-words">
        <span className="font-medium">{label}: </span>
        <span className="whitespace-pre-wrap">
          {typeof value === "string" ? JSON.stringify(value) : String(value)}
        </span>
      </div>
    )
  }
  const entries = Object.entries(value)
  return (
    <details
      open={open}
      onToggle={(event) => setOpen(event.currentTarget.open)}
      className="my-1"
    >
      <summary className="cursor-pointer break-words">
        {label}: {Array.isArray(value) ? "Array" : "Object"} ({entries.length})
      </summary>
      {open && (
        <div className="ml-4 border-l pl-3">
          {entries.slice(0, visible).map(([key, child]) => (
            <JsonNode
              key={key}
              label={Array.isArray(value) ? `[${key}]` : JSON.stringify(key)}
              value={child}
            />
          ))}
          {visible < entries.length && (
            <Button
              variant="outline"
              size="sm"
              onClick={() => setVisible((count) => count + 100)}
            >
              Show next {Math.min(100, entries.length - visible)} entries
            </Button>
          )}
        </div>
      )}
    </details>
  )
}

function JsonContent({ text, title }: { text: string; title: string }) {
  let value: unknown
  try {
    value = JSON.parse(text)
  } catch {
    return (
      <ErrorNotice error={new Error("Report content is not valid JSON.")} />
    )
  }
  return <JsonNode value={value} label={title} root />
}

export default function JsonReport({ content, title }: ReportRendererProps) {
  return (
    <BlobText content={content}>
      {(text) => <JsonContent key={text} text={text} title={title} />}
    </BlobText>
  )
}
