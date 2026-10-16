import { lazy, type ComponentType, type LazyExoticComponent } from "react"
import type { ReportRendererProps } from "./types"

export const rendererRegistry: ReadonlyMap<
  string,
  LazyExoticComponent<ComponentType<ReportRendererProps>>
> = new Map([
  ["markdown", lazy(() => import("./markdown/MarkdownReport"))],
  ["text", lazy(() => import("./text/TextReport"))],
  ["json", lazy(() => import("./json/JsonReport"))],
  ["image", lazy(() => import("./image/ImageReport"))],
  ["html", lazy(() => import("./html/HtmlReport"))],
  ["text/html", lazy(() => import("./html/HtmlReport"))],
  ["svg", lazy(() => import("./html/HtmlReport"))],
  ["image/svg+xml", lazy(() => import("./html/HtmlReport"))],
])
