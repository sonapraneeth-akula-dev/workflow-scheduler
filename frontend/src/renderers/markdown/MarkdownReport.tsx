import Markdown from "react-markdown"
import { BlobText } from "../BlobText"
import type { ReportRendererProps } from "../types"

function safeLink(value: string) {
  if (value.startsWith("#")) return value
  try {
    const url = new URL(value)
    return url.protocol === "https:" || url.protocol === "http:" ? url.href : ""
  } catch {
    return ""
  }
}

export default function MarkdownReport({
  content,
  title,
}: ReportRendererProps) {
  return (
    <BlobText content={content}>
      {(text) => (
        <article
          aria-label={title}
          className="space-y-3 break-words [&_a]:text-primary [&_a]:underline [&_blockquote]:border-l-2 [&_blockquote]:pl-3 [&_h1]:text-2xl [&_h2]:text-xl [&_h3]:text-lg [&_li]:ml-5 [&_ol]:list-decimal [&_pre]:overflow-auto [&_pre]:rounded [&_pre]:bg-muted [&_pre]:p-3 [&_ul]:list-disc"
        >
          <Markdown
            skipHtml
            urlTransform={(url, key) => (key === "href" ? safeLink(url) : "")}
            components={{
              a: ({ children, href }) =>
                href ? (
                  <a href={href} target="_blank" rel="noopener noreferrer">
                    {children}
                  </a>
                ) : (
                  <span>{children} (link blocked)</span>
                ),
              img: ({ alt }) => (
                <span>{alt || "Report image"} (inline image blocked)</span>
              ),
            }}
          >
            {text}
          </Markdown>
        </article>
      )}
    </BlobText>
  )
}
