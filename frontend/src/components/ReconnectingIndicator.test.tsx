import { fireEvent, render, screen } from "@testing-library/react"
import { describe, expect, it, vi } from "vitest"
import { ReconnectingIndicator } from "./ReconnectingIndicator"

describe("run reconnect status", () => {
  it("announces recovery without hiding the cause of failure", () => {
    const retry = vi.fn()
    const view = render(
      <ReconnectingIndicator
        status="reconnecting"
        run={null}
        error={null}
        retry={retry}
      />
    )
    expect(screen.getByRole("status")).toHaveTextContent(
      "Reconnecting and restoring"
    )
    view.rerender(
      <ReconnectingIndicator
        status="error"
        run={null}
        error={new Error("Run access revoked")}
        retry={retry}
      />
    )
    expect(screen.getByRole("alert")).toHaveTextContent("Run access revoked")
    fireEvent.click(screen.getByRole("button", { name: "Retry run updates" }))
    expect(retry).toHaveBeenCalledOnce()
    view.rerender(
      <ReconnectingIndicator
        status="live"
        run={null}
        error={null}
        retry={retry}
      />
    )
    expect(screen.queryByRole("status")).not.toBeInTheDocument()
    expect(screen.queryByRole("alert")).not.toBeInTheDocument()
  })
})
