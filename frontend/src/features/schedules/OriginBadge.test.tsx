import { render, screen } from "@testing-library/react"
import { afterEach, expect, it, vi } from "vitest"
import { OriginBadge } from "./OriginBadge"

const code = {
  origin: "Code",
  sourcePath: "workflows/nightly.schedules.json",
  gitCommit: "a".repeat(40),
} as const
afterEach(() => vi.unstubAllEnvs())

it("shows UI origin without inventing repository provenance", () => {
  render(
    <OriginBadge
      schedule={{ origin: "User", sourcePath: null, gitCommit: null }}
    />
  )
  expect(screen.getByText("UI schedule")).toBeInTheDocument()
  expect(screen.queryByRole("link")).not.toBeInTheDocument()
})

it.each([40, 64])(
  "shows the applied file and safe full %i-character commit link",
  (length) => {
    vi.stubEnv(
      "VITE_COMMIT_URL_TEMPLATE",
      "https://github.com/example/workflows/commit/{commit}"
    )
    const commit = "b".repeat(length)
    render(<OriginBadge schedule={{ ...code, gitCommit: commit }} />)
    expect(screen.getByText(code.sourcePath)).toBeInTheDocument()
    const link = screen.getByRole("link", {
      name: `View schedule commit ${commit} (opens in a new tab)`,
    })
    expect(link).toHaveAttribute(
      "href",
      `https://github.com/example/workflows/commit/${commit}`
    )
    expect(link).toHaveAttribute("rel", "noopener noreferrer")
  }
)

it("keeps provenance visible and reports an unconfigured commit browser explicitly", () => {
  vi.stubEnv("VITE_COMMIT_URL_TEMPLATE", "")
  render(<OriginBadge schedule={code} />)
  expect(screen.getByText(/Commit link unavailable/)).toBeInTheDocument()
  expect(screen.queryByRole("link")).not.toBeInTheDocument()
})

it("shows explicit legacy absence instead of fabricating a path or commit", () => {
  render(
    <OriginBadge
      schedule={{ origin: "Code", sourcePath: null, gitCommit: null }}
    />
  )
  expect(screen.getByText(/Source provenance unavailable/)).toBeInTheDocument()
  expect(screen.getByText("Commit unavailable.")).toBeInTheDocument()
})

it.each([
  "javascript:alert('{commit}')",
  "http://example.com/commit/{commit}",
  "https://user:password@example.com/commit/{commit}",
  "https://example.com/commit/no-placeholder",
  "https://example.com/{commit}/{commit}",
  "not-a-url/{commit}",
])("rejects unsafe or ambiguous commit browser template %s", (template) => {
  vi.stubEnv("VITE_COMMIT_URL_TEMPLATE", template)
  render(<OriginBadge schedule={code} />)
  expect(screen.getByRole("alert")).toBeInTheDocument()
  expect(screen.queryByRole("link")).not.toBeInTheDocument()
})
