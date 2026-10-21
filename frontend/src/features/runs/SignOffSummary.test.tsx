import { render, screen } from "@testing-library/react"
import { describe, expect, it, vi } from "vitest"
import { SignOffSummary } from "./SignOffSummary"
import type { SignOffState } from "./signoff-api"

vi.mock("@tanstack/react-router", () => ({
  Link: ({
    children,
    params,
    hash,
    ...props
  }: {
    children: React.ReactNode
    params: { id: string }
    hash: string
  }) => (
    <a {...props} href={`/runs/${params.id}#${hash}`}>
      {children}
    </a>
  ),
}))
const state: SignOffState = {
  revision: "2",
  status: "Revoked",
  latest: {
    revision: "2",
    action: "Revoke",
    reason: "Regression <script> found",
    actorId: "11111111-1111-4111-8111-111111111111",
    actorName: "Second reviewer",
    at: "2026-10-21T00:00:00Z",
  },
}

describe("sign-off summaries", () => {
  it("shows current revocation, authenticated identity, time and reason with a direct history link", () => {
    render(<SignOffSummary state={state} runId="run-id" />)
    expect(screen.getByText("Revoked")).toBeVisible()
    expect(
      screen.getByText("Second reviewer: Regression <script> found")
    ).toBeVisible()
    expect(screen.getByRole("link")).toHaveAttribute(
      "href",
      "/runs/run-id#signoff"
    )
    expect(screen.getByRole("link")).toHaveAccessibleName(
      expect.stringContaining(state.latest!.actorId)
    )
    expect(document.querySelector("time")).toHaveAttribute(
      "dateTime",
      state.latest!.at
    )
    expect(document.querySelector("script")).toBeNull()
  })
  it("distinguishes an unsigned run from a revoked approval without inventing a reviewer", () => {
    render(
      <SignOffSummary
        state={{ revision: "0", status: "NotSignedOff", latest: null }}
        runId="run-id"
        compact
      />
    )
    expect(screen.getByText("Not signed off")).toBeVisible()
    expect(screen.getByRole("link")).toHaveAccessibleName(
      "Not signed off. View sign-off details and history."
    )
    expect(document.querySelector("time")).toBeNull()
  })
})
