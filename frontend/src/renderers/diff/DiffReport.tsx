import { useEffect, useRef } from "react"
import * as monaco from "monaco-editor/editor/editor.api.js"
import EditorWorker from "monaco-editor/editor/editor.worker.js?worker"
import { z } from "zod"
import { ErrorNotice } from "@/components/ErrorNotice"
import { BlobText } from "../BlobText"
import type { ReportRendererProps } from "../types"

globalThis.MonacoEnvironment ??= { getWorker: () => new EditorWorker() }
const schema = z.strictObject({ original: z.string(), modified: z.string() })

function Editor({
  original,
  modified,
  title,
}: z.infer<typeof schema> & { title: string }) {
  const target = useRef<HTMLDivElement>(null)
  useEffect(() => {
    let before: monaco.editor.ITextModel | undefined
    let after: monaco.editor.ITextModel | undefined
    const theme = () =>
      document.documentElement.classList.contains("dark") ? "vs-dark" : "vs"
    let editor: monaco.editor.IStandaloneDiffEditor | undefined
    const subscriptions: monaco.IDisposable[] = []
    const observer = new MutationObserver(() => monaco.editor.setTheme(theme()))
    try {
      before = monaco.editor.createModel(original, "plaintext")
      after = monaco.editor.createModel(modified, "plaintext")
      editor = monaco.editor.createDiffEditor(target.current!, {
        readOnly: true,
        domReadOnly: true,
        editContext: false,
        originalEditable: false,
        automaticLayout: true,
        renderSideBySide: true,
        useInlineViewWhenSpaceIsLimited: true,
        minimap: { enabled: false },
        scrollBeyondLastLine: false,
        links: false,
        accessibilitySupport: "on",
        originalAriaLabel: `${title}: original text`,
        modifiedAriaLabel: `${title}: modified text`,
        theme: theme(),
      })
      editor.setModel({ original: before, modified: after })
      // Monaco 0.57 resets sub-editor labels during responsive diff layout.
      for (const [inner, label] of [
        [editor.getOriginalEditor(), `${title}: original text`],
        [editor.getModifiedEditor(), `${title}: modified text`],
      ] as const) {
        const option = monaco.editor.EditorOption.ariaLabel
        inner.updateOptions({ ariaLabel: label })
        subscriptions.push(
          inner.onDidChangeConfiguration((event) => {
            if (event.hasChanged(option) && inner.getOption(option) !== label)
              inner.updateOptions({ ariaLabel: label })
          })
        )
      }
      observer.observe(document.documentElement, {
        attributes: true,
        attributeFilter: ["class"],
      })
    } catch (error) {
      subscriptions.forEach((subscription) => subscription.dispose())
      editor?.dispose()
      before?.dispose()
      after?.dispose()
      observer.disconnect()
      throw error
    }
    return () => {
      subscriptions.forEach((subscription) => subscription.dispose())
      observer.disconnect()
      editor?.dispose()
      before?.dispose()
      after?.dispose()
    }
  }, [original, modified, title])
  return (
    <section aria-label={`${title} diff`} className="min-w-0 space-y-3">
      <div ref={target} className="h-[32rem] min-w-0 rounded-md border" />
      <details>
        <summary className="cursor-pointer">
          Plain-text diff alternatives
        </summary>
        <h3 className="font-semibold">Original</h3>
        <pre className="overflow-auto break-words whitespace-pre-wrap">
          {original}
        </pre>
        <h3 className="font-semibold">Modified</h3>
        <pre className="overflow-auto break-words whitespace-pre-wrap">
          {modified}
        </pre>
      </details>
    </section>
  )
}

function DiffContent({ text, title }: { text: string; title: string }) {
  let value: z.infer<typeof schema>
  try {
    value = schema.parse(JSON.parse(text))
  } catch {
    return (
      <ErrorNotice
        error={
          new Error("Diff JSON requires original and modified text strings.")
        }
      />
    )
  }
  return <Editor {...value} title={title} />
}

export default function DiffReport({ content, title }: ReportRendererProps) {
  return (
    <BlobText content={content}>
      {(text) => <DiffContent key={text} text={text} title={title} />}
    </BlobText>
  )
}
