import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { fireEvent, render, screen } from "@testing-library/react"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import { getTimeline, type RunTimeline } from "./timeline-api"
import { Timeline } from "./Timeline"

vi.mock("./timeline-api", async (original) => ({
  ...(await original<typeof import("./timeline-api")>()),
  getTimeline: vi.fn(),
}))
const runId = "019ed132-2f44-7000-8000-000000000001"
const timeline: RunTimeline = {
  runId,
  lastSeq: 12,
  status: "Running",
  attempts: [
    {
      nodeKey: "work",
      taskKey: "work/http",
      attempt: 1,
      queuedAt: "2026-10-16T08:00:00Z",
      dispatchedAt: "2026-10-16T08:00:01Z",
      startedAt: "2026-10-16T08:00:02Z",
      endedAt: "2026-10-16T08:00:04Z",
      queueWaitMs: 2000,
      durationMs: 2000,
      terminalStatus: "Failed",
      failureCategory: "TaskError",
      agentId: "original-agent",
    },
    {
      nodeKey: "work",
      taskKey: "work/http",
      attempt: 2,
      queuedAt: "2026-10-16T08:00:05Z",
      dispatchedAt: "2026-10-16T08:00:06Z",
      startedAt: "2026-10-16T08:00:07Z",
      endedAt: null,
      queueWaitMs: 2000,
      durationMs: 0,
      terminalStatus: null,
      failureCategory: null,
      agentId: null,
    },
  ],
}
function show() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  return {
    ...render(
      <QueryClientProvider client={client}>
        <Timeline runId={runId} />
      </QueryClientProvider>
    ),
    client,
  }
}
describe("run attempt timeline", () => {
  beforeEach(() => vi.resetAllMocks())
  it("separates queue/execution bars while retaining all exact rows in the accessible table", async () => {
    vi.mocked(getTimeline).mockResolvedValue(timeline)
    const { container } = show()
    const table = await screen.findByRole("table")
    expect(screen.getAllByRole("row")).toHaveLength(3)
    expect(table).toHaveTextContent("original-agent")
    expect(table).toHaveTextContent("Not recorded")
    expect(table).toHaveTextContent("TaskError")
    expect(table).toHaveTextContent("0 ms recorded; not completed")
    expect(container.querySelectorAll("[data-timeline-queue]")).toHaveLength(2)
    expect(
      container.querySelectorAll("[data-timeline-execution]")
    ).toHaveLength(1)
    expect(container.querySelector("[data-timeline-active]")).toHaveTextContent(
      "Execution active; no end"
    )
    expect(screen.getByRole("img")).toHaveAccessibleName(
      /Queue \(Q\) and execution \(E\) intervals for 2/
    )
    const firstQueue = container.querySelector("[data-timeline-queue]")
    expect(firstQueue).toHaveStyle({ left: "0%" })
    expect(screen.getByText("Exact attempt table")).toHaveAttribute(
      "class",
      expect.stringContaining("cursor-pointer")
    )
    fireEvent.click(screen.getByText("Exact attempt table"))
    expect(table).not.toBeVisible()
    fireEvent.click(screen.getByText("Exact attempt table"))
    expect(screen.getByRole("table")).toBeVisible()
  })
  it("announces genuinely empty dispatched history without invented attempt rows", async () => {
    vi.mocked(getTimeline).mockResolvedValue({ ...timeline, attempts: [] })
    show()
    expect(screen.getByText("Loading committed timeline...")).toBeVisible()
    expect(
      await screen.findByText("No dispatched attempts recorded.")
    ).toBeVisible()
    expect(screen.queryByRole("table")).not.toBeInTheDocument()
    expect(screen.queryByRole("img")).not.toBeInTheDocument()
  })
  it("displays explicit recorded-clock ordering caveats instead of negative chart intervals", async () => {
    vi.mocked(getTimeline).mockResolvedValue({
      ...timeline,
      attempts: [
        { ...timeline.attempts[0]!, dispatchedAt: "2026-10-16T07:59:59Z" },
      ],
    })
    const { container } = show()
    expect(
      await screen.findByText(
        /Some recorded timestamps have differing clock order/
      )
    ).toHaveAttribute("role", "status")
    expect(
      container.querySelector("[data-timeline-queue]")
    ).not.toBeInTheDocument()
    expect(screen.getByRole("table")).toHaveTextContent("2026-10-16T07:59:59Z")
  })
  it.each([401, 403, 503])(
    "hides stale data after failed refresh HTTP %i and surfaces the error",
    async (status) => {
      vi.mocked(getTimeline).mockResolvedValue(timeline)
      show()
      await screen.findByRole("table")
      vi.mocked(getTimeline).mockRejectedValue(
        new ApiError(new Response(null, { status }), {
          title: "Timeline unavailable.",
        })
      )
      fireEvent.click(screen.getByRole("button", { name: "Refresh timeline" }))
      expect(await screen.findByRole("alert")).toHaveTextContent(
        "Timeline unavailable."
      )
      expect(screen.queryByRole("table")).not.toBeInTheDocument()
      expect(
        screen.queryByText("No dispatched attempts recorded.")
      ).not.toBeInTheDocument()
    }
  )
})
