import { render, screen } from "@testing-library/react"
import { expect, it } from "vitest"
import { TriggerBadge } from "./TriggerBadge"

it.each([
  ["Manual", "Manual"],
  ["Schedule", "Scheduled"],
  ["CatchUp", "Catch-up"],
] as const)(
  "labels the %s trigger without changing its outcome",
  (trigger, label) => {
    render(<TriggerBadge trigger={trigger} />)
    expect(screen.getByText(label)).toBeInTheDocument()
    if (trigger === "CatchUp") {
      expect(screen.getByText(label)).toHaveAttribute(
        "title",
        "Recovery run for the latest missed scheduled fire"
      )
    }
  }
)
