import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import type { ReactNode } from "react"
import { fireEvent, render, screen } from "@testing-library/react"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { getSeriesFires } from "@/lib/api/client"
import { SeriesFiresTable } from "./SeriesFiresTable"

vi.mock("@/lib/api/client", () => ({ getSeriesFires: vi.fn() }))
vi.mock("@tanstack/react-router", () => ({
  Link: ({
    params,
    children,
  }: {
    params: { id: string }
    children: ReactNode
  }) => <a href={`/runs/${params.id}`}>{children}</a>,
}))
const seriesId = "a77bb9c1-eebc-4145-a63b-296d62ee8747"
const read = vi.mocked(getSeriesFires)

function show() {
  return render(
    <QueryClientProvider
      client={
        new QueryClient({ defaultOptions: { queries: { retry: false } } })
      }
    >
      <SeriesFiresTable seriesId={seriesId} />
    </QueryClientProvider>
  )
}

describe("series fire history states", () => {
  beforeEach(() => vi.resetAllMocks())
  it("badges a linked catch-up run while keeping its source outcome Missed", async () => {
    read.mockResolvedValue({
      seriesId,
      items: [
        {
          fireAt: "2026-10-15T12:00:00Z",
          outcome: "Missed",
          runId: "019ed132-2f44-7000-8000-000000000001",
          reason: "catch-up",
        },
      ],
      nextCursor: null,
    })
    show()
    expect(await screen.findByRole("table")).toHaveTextContent("Missed")
    expect(screen.getByText("Catch-up")).toHaveAttribute(
      "title",
      "Recovery run for the latest missed scheduled fire"
    )
    expect(screen.getByRole("link")).toHaveAttribute(
      "href",
      "/runs/019ed132-2f44-7000-8000-000000000001"
    )
  })
  it("announces loading and then a genuinely empty history", async () => {
    read.mockResolvedValue({ seriesId, items: [], nextCursor: null })
    show()
    expect(
      screen.getByRole("status", { name: "Loading fire history" })
    ).toBeInTheDocument()
    expect(await screen.findByText("No recorded fires.")).toBeInTheDocument()
    expect(screen.queryByRole("table")).not.toBeInTheDocument()
  })
  it("surfaces failed reads rather than claiming an empty history", async () => {
    read.mockRejectedValue(new Error("History access denied."))
    show()
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "History access denied."
    )
    expect(screen.queryByText("No recorded fires.")).not.toBeInTheDocument()
  })
  it("retains loaded skips when loading older history fails, then allows refresh", async () => {
    read
      .mockResolvedValueOnce({
        seriesId,
        items: [
          {
            fireAt: "2026-10-15T12:00:00.000001Z",
            outcome: "Skipped",
            runId: null,
            reason: "overlap",
          },
        ],
        nextCursor: "AAAAAAAAAAA",
      })
      .mockRejectedValueOnce(new Error("Older history unavailable."))
    show()
    expect(await screen.findByRole("table")).toHaveTextContent("Skipped")
    fireEvent.click(screen.getByRole("button", { name: "Load older fires" }))
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Older history unavailable."
    )
    expect(screen.getByRole("table")).toHaveTextContent("overlap")
    expect(read.mock.calls[1]?.[1]).toBe("AAAAAAAAAAA")
    read.mockResolvedValue({ seriesId, items: [], nextCursor: null })
    fireEvent.click(screen.getByRole("button", { name: "Refresh history" }))
    expect(await screen.findByText("No recorded fires.")).toBeInTheDocument()
  })
})
