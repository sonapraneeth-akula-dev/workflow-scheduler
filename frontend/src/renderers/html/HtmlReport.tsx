import { BlobText } from "../BlobText"
import type { ReportRendererProps } from "../types"

const policy =
  "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
  "img-src data: blob:; font-src data:; connect-src 'none'; form-action 'none'; base-uri 'none'"

export default function HtmlReport({ content, title }: ReportRendererProps) {
  return (
    <BlobText content={content}>
      {(text) => (
        <iframe
          title={`${title} (sandboxed report)`}
          sandbox="allow-scripts"
          referrerPolicy="no-referrer"
          className="h-[32rem] w-full rounded-md border bg-white"
          srcDoc={`<!doctype html><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="${policy}">${text}`}
        />
      )}
    </BlobText>
  )
}
