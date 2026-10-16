import type { ReactNode } from "react"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { act, fireEvent, render, screen } from "@testing-library/react"
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import { getReservationQueue, type ReservationQueue } from "./reservation-api"
import { QueuedBanner } from "./QueuedBanner"

vi.mock("./reservation-api", () => ({ getReservationQueue: vi.fn() }))
vi.mock("@tanstack/react-router", () => ({
  Link: ({
    params,
    children,
    className,
  }: {
    params: { id: string }
    children: ReactNode
    className?: string
  }) => (
    <a href={`/runs/${params.id}`} className={className}>
      {children}
    </a>
  ),
}))
const runId = "019ed132-2f44-7000-8000-000000000001"
const result: ReservationQueue = {
  runId,
  items: [
    {
      nodeKey: "work",
      position: 2,
      count: 1,
      queuedAt: "2026-10-16T08:00:00Z",
      blockingReservationId: "019ed132-2f44-7000-8000-000000000002",
      blockingRunId: "019ed132-2f44-7000-8000-000000000003",
    },
  ],
}
function show(active = true) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  const view = render(
    <QueryClientProvider client={client}>
      <QueuedBanner runId={runId} active={active} />
    </QueryClientProvider>
  )
  return { client, ...view }
}
describe("QueuedBanner", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    vi.mocked(getReservationQueue).mockResolvedValue(result)
  })
  afterEach(() => vi.useRealTimers())
  it("renders position and blocking run/reservation with a keyboard-accessible run link", async () => {
    show()
    const banner = await screen.findByRole("region", {
      name: "Reservation queue",
    })
    expect(banner).toHaveAttribute("aria-live", "polite")
    expect(banner).toHaveTextContent(
      "Node work: queue position 2. Needs 1 machine."
    )
    expect(banner).toHaveTextContent(result.items[0]!.blockingReservationId!)
    const link = screen.getByRole("link", {
      name: result.items[0]!.blockingRunId!,
    })
    expect(link).toHaveAttribute(
      "href",
      `/runs/${result.items[0]!.blockingRunId}`
    )
    link.focus()
    expect(link).toHaveFocus()
  })
  it("does not invent a blocker when its identity is redacted", async () => {
    vi.mocked(getReservationQueue).mockResolvedValue({
      ...result,
      items: [
        {
          ...result.items[0]!,
          blockingReservationId: null,
          blockingRunId: null,
        },
      ],
    })
    show()
    expect(
      await screen.findByText(/Waiting for matching free machines/)
    ).toBeVisible()
    expect(screen.queryByRole("link")).not.toBeInTheDocument()
  })
  it("updates automatically at two seconds and disappears when granted", async () => {
    vi.useFakeTimers()
    show()
    await act(async () => {
      await vi.advanceTimersByTimeAsync(20)
    })
    expect(screen.getByText(/queue position 2/)).toBeVisible()
    vi.mocked(getReservationQueue).mockResolvedValue({
      ...result,
      items: [{ ...result.items[0]!, position: 1 }],
    })
    await act(async () => {
      await vi.advanceTimersByTimeAsync(2_000)
    })
    expect(screen.getByText(/queue position 1/)).toBeVisible()
    vi.mocked(getReservationQueue).mockResolvedValue({ runId, items: [] })
    await act(async () => {
      await vi.advanceTimersByTimeAsync(2_000)
    })
    expect(
      screen.queryByRole("region", { name: "Reservation queue" })
    ).not.toBeInTheDocument()
  })
  it.each([401, 403, 503])(
    "hides stale blocker data on HTTP %i and supports explicit retry",
    async (status) => {
      const { client } = show()
      await screen.findByText(/queue position 2/)
      vi.mocked(getReservationQueue).mockRejectedValue(
        new ApiError(new Response(null, { status }), {
          title: "Queue unavailable",
        })
      )
      await act(async () => {
        await client.invalidateQueries({
          queryKey: ["reservation-queue", runId],
        })
      })
      expect(await screen.findByText("Queue unavailable")).toBeVisible()
      expect(screen.queryByText(/queue position 2/)).not.toBeInTheDocument()
      expect(screen.queryByRole("link")).not.toBeInTheDocument()
      vi.mocked(getReservationQueue).mockResolvedValue(result)
      fireEvent.click(
        screen.getByRole("button", { name: "Retry reservation queue" })
      )
      expect(await screen.findByText(/queue position 2/)).toBeVisible()
    }
  )
  it("does not request or display waits for terminal runs", () => {
    show(false)
    expect(getReservationQueue).not.toHaveBeenCalled()
    expect(
      screen.queryByRole("region", { name: "Reservation queue" })
    ).not.toBeInTheDocument()
  })
  it("stops interval polling after access is revoked", async () => {
    vi.useFakeTimers()
    vi.mocked(getReservationQueue).mockRejectedValue(
      new ApiError(new Response(null, { status: 403 }), {
        title: "Queue forbidden",
      })
    )
    show()
    await act(async () => {
      await vi.advanceTimersByTimeAsync(20)
    })
    expect(screen.getByText("Queue forbidden")).toBeVisible()
    expect(getReservationQueue).toHaveBeenCalledTimes(1)
    await act(async () => {
      await vi.advanceTimersByTimeAsync(10_000)
    })
    expect(getReservationQueue).toHaveBeenCalledTimes(1)
  })
})
