import { useState } from "react"
import { Button } from "@/components/ui/button"
import { ErrorNotice } from "@/components/ErrorNotice"
import { JsonNumberSupportError, parseJson } from "@/lib/json"
import { BlobText } from "../BlobText"
import type { ReportRendererProps } from "../types"

export function JsonTree({
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
  const rawNumber =
    typeof JSON.isRawJSON === "function" && JSON.isRawJSON(value)
      ? value.rawJSON
      : undefined
  if (value === null || typeof value !== "object" || rawNumber !== undefined) {
    return (
      <div className="break-words">
        <span className="font-medium">{label}: </span>
        <span className="whitespace-pre-wrap">
          {rawNumber ??
            (typeof value === "string" ? JSON.stringify(value) : String(value))}
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
            <JsonTree
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
    value = parseJson(text)
  } catch (error) {
    return (
      <ErrorNotice
        error={
          error instanceof JsonNumberSupportError
            ? error
            : new Error("Report content is not valid JSON.")
        }
      />
    )
  }
  return <JsonTree value={value} label={title} root />
}

export default function JsonReport({ content, title }: ReportRendererProps) {
  return (
    <BlobText content={content}>
      {(text) => <JsonContent key={text} text={text} title={title} />}
    </BlobText>
  )
}
