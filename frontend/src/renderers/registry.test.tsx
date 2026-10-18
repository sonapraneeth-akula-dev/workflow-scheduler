import { render, screen } from "@testing-library/react"
import { describe, expect, it, vi } from "vitest"
import { ReportRenderer } from "./ReportRenderer"
import { rendererRegistry } from "./registry"
import type { ReportRendererProps } from "./types"

vi.mock("./registry", async (original) => {
  const actual = await original<typeof import("./registry")>()
  const { lazy } = await import("react")
  const example = lazy(async () => ({
    default: ({ title, content }: ReportRendererProps) => (
      <p role="status">
        {title}: {content.size} bytes
      </p>
    ),
  }))
  return {
    rendererRegistry: new Map([
      ...actual.rendererRegistry,
      ["example", example],
    ]),
  }
})

describe("renderer-only extension", () => {
  it("renders a new kind through one lazy registry entry without a backend change", async () => {
    render(
      <ReportRenderer
        kind="example"
        title="Additional kind"
        content={new Blob(["sample"])}
      />
    )
    expect(await screen.findByText("Additional kind: 6 bytes")).toHaveAttribute(
      "role",
      "status"
    )
  })
  it("retains existing kinds and stable component identities alongside the entry", () => {
    for (const kind of ["markdown", "text", "json", "image", "example"]) {
      const component = rendererRegistry.get(kind)
      expect(component).toBeDefined()
      expect(rendererRegistry.get(kind)).toBe(component)
    }
    expect(rendererRegistry.get("__proto__")).toBeUndefined()
    expect(rendererRegistry.get("Example")).toBeUndefined()
  })
})
