import { Component, createElement, Suspense, type ReactNode } from "react"
import { ErrorNotice } from "@/components/ErrorNotice"
import { rendererRegistry } from "./registry"
import type { ReportRendererProps } from "./types"

interface BoundaryProps extends ReportRendererProps {
  children: ReactNode
  kind: string
}

class RendererBoundary extends Component<
  BoundaryProps,
  { content: Blob; kind: string; error: unknown }
> {
  state = { content: this.props.content, kind: this.props.kind, error: null }

  static getDerivedStateFromProps(
    props: BoundaryProps,
    state: { content: Blob; kind: string }
  ) {
    return props.content === state.content && props.kind === state.kind
      ? null
      : { content: props.content, kind: props.kind, error: null }
  }

  static getDerivedStateFromError(error: unknown) {
    return { error }
  }

  render() {
    return this.state.error !== null ? (
      <ErrorNotice error={this.state.error} />
    ) : (
      this.props.children
    )
  }
}

export function ReportRenderer({
  kind,
  ...props
}: ReportRendererProps & { kind: string }) {
  const Renderer = rendererRegistry.get(kind)
  if (!Renderer) {
    return <p role="status">Unsupported report kind: {kind}.</p>
  }
  return (
    <RendererBoundary {...props} kind={kind}>
      <Suspense fallback={<p role="status">Loading report renderer...</p>}>
        {createElement(Renderer, props)}
      </Suspense>
    </RendererBoundary>
  )
}
