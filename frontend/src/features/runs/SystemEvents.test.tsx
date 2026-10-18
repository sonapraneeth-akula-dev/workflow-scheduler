import type { AnchorHTMLAttributes } from "react"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import type { LiveSnapshot } from "@/lib/live/subscriptions"
import { getSystemEvents, type SystemEventsPage } from "./system-events-api"
import { SystemEvents } from "./SystemEvents"

const state = vi.hoisted(() => ({
  snapshot: undefined as LiveSnapshot | undefined,
  retry: vi.fn(),
}))
vi.mock("@/lib/live/useLive", () => ({
  useLive: () => ({ snapshot: state.snapshot, retry: state.retry }),
}))
vi.mock("@tanstack/react-router", () => ({
  Link: ({
    children,
    to,
    params,
    ...props
  }: AnchorHTMLAttributes<HTMLAnchorElement> & {
    to: string
    params: Record<string, string>
  }) => (
    <a
      {...props}
      href={Object.entries(params).reduce(
        (path, [key, value]) =>
          path.replace(`$${key}`, encodeURIComponent(value)),
        to
      )}
    >
      {children}
    </a>
  ),
}))
vi.mock("./system-events-api", async (original) => ({
  ...(await original<typeof import("./system-events-api")>()),
  getSystemEvents: vi.fn(),
}))
const runId = "019ed132-2f44-7000-8000-000000000001"
const page: SystemEventsPage = {
  runId,
  workflowId: "team/type/workflow",
  versionId: "019ed132-2f44-7000-8000-000000000002",
  lastSeq: "20",
  nextCursor: null,
  events: [
    {
      seq: "7",
      at: "2026-10-18T00:00:00Z",
      entity: 2,
      key: "node/task",
      state: 2,
      attempt: 2,
      agentId: "019ed132-2f44-7000-8000-000000000003",
      selectionReason: "LeastLoaded",
      plannedAgentId: "019ed132-2f44-7000-8000-000000000004",
      waitingReason: null,
      failureCategory: null,
      progress: null,
    },
  ],
}
function show() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  const tree = () => (
    <QueryClientProvider client={client}>
      <SystemEvents runId={runId} />
    </QueryClientProvider>
  )
  const view = render(tree())
  return { ...view, client, update: () => view.rerender(tree()) }
}
describe("read-only system events", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    state.snapshot = {
      resource: { scope: "run", id: runId },
      status: "live",
      error: null,
      delta: null,
      resyncVersion: 0,
      reportVersion: 0,
    }
    vi.mocked(getSystemEvents).mockResolvedValue(page)
  })
  it("distinguishes prior planned pin from actual agent and links the pinned historical task plan", async () => {
    show()
    const table = await screen.findByRole("table")
    expect(table).toHaveTextContent(page.events[0]!.agentId!)
    expect(table).toHaveTextContent(page.events[0]!.plannedAgentId!)
    expect(table).toHaveTextContent("LeastLoaded")
    expect(
      screen.getByRole("columnheader", { name: "Planned agent (prior pin)" })
    ).toBeVisible()
    expect(
      screen.getByRole("columnheader", { name: "Selected agent" })
    ).toBeVisible()
    expect(
      screen.getByRole("link", { name: "Pinned Task Plan" })
    ).toHaveAttribute(
      "href",
      `/t/team/type/w/workflow/versions/${page.versionId}`
    )
    expect(
      screen.getByRole("link", { name: "Declared task plan" })
    ).toHaveAttribute(
      "href",
      `/t/team/type/w/workflow/versions/${page.versionId}/tasks/node%2Ftask`
    )
    expect(table).not.toHaveTextContent("private-")
  })
  it("applies exact node/task/attempt filters and rejects conflicting input without a request", async () => {
    show()
    await screen.findByRole("table")
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Refresh system events" })
      ).toBeEnabled()
    )
    fireEvent.change(screen.getByLabelText("Node key"), {
      target: { value: "other" },
    })
    fireEvent.change(screen.getByLabelText("Full task key"), {
      target: { value: "node/task" },
    })
    const before = vi.mocked(getSystemEvents).mock.calls.length
    fireEvent.click(screen.getByRole("button", { name: "Apply event filters" }))
    expect(await screen.findByRole("alert")).toHaveTextContent(/must agree/)
    expect(getSystemEvents).toHaveBeenCalledTimes(before)
    fireEvent.change(screen.getByLabelText("Node key"), {
      target: { value: "node" },
    })
    fireEvent.change(screen.getByLabelText("Attempt"), {
      target: { value: "2" },
    })
    fireEvent.click(screen.getByRole("button", { name: "Apply event filters" }))
    await waitFor(() =>
      expect(getSystemEvents).toHaveBeenLastCalledWith(
        runId,
        { node: "node", task: "node/task", attempt: "2" },
        undefined,
        expect.any(AbortSignal)
      )
    )
    expect(screen.queryByRole("alert")).not.toBeInTheDocument()
  })
  it("paginates in order and renders explicit legacy unknowns and remote transitions, not polls", async () => {
    const cursor = "a".repeat(120)
    vi.mocked(getSystemEvents).mockImplementation(async (_, __, next) =>
      next
        ? {
            ...page,
            events: [
              {
                ...page.events[0]!,
                seq: "8",
                state: 3,
                agentId: null,
                selectionReason: null,
                plannedAgentId: null,
                progress: {
                  sequence: 1,
                  phase: 3,
                  occurredAt: "2026-10-18T00:00:00Z",
                },
              },
            ],
          }
        : {
            ...page,
            nextCursor: cursor,
            events: [
              {
                ...page.events[0]!,
                agentId: null,
                selectionReason: null,
                plannedAgentId: null,
              },
            ],
          }
    )
    show()
    expect(await screen.findByRole("table")).toHaveTextContent(
      "Not recorded (legacy)"
    )
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Load more system events" })
      ).toBeEnabled()
    )
    fireEvent.click(
      screen.getByRole("button", { name: "Load more system events" })
    )
    expect(await screen.findByText("Remote: Waiting")).toBeVisible()
    expect(
      screen.getAllByRole("rowheader").map((row) => row.textContent)
    ).toEqual(["7", "8"])
    expect(
      screen.queryByRole("button", { name: "Load more system events" })
    ).not.toBeInTheDocument()
  })
  it("resyncs through pushed invalidation without polling and catches a delta arriving during a read", async () => {
    const shown = show()
    await screen.findByRole("table")
    await waitFor(() =>
      expect(
        screen.getByRole("button", { name: "Refresh system events" })
      ).toBeEnabled()
    )
    let finish: ((value: SystemEventsPage) => void) | undefined
    vi.mocked(getSystemEvents).mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          finish = resolve
        })
    )
    state.snapshot = { ...state.snapshot!, resyncVersion: 1 }
    shown.update()
    await waitFor(() => expect(finish).toBeDefined())
    vi.mocked(getSystemEvents).mockResolvedValue({
      ...page,
      events: [{ ...page.events[0]!, seq: "9", selectionReason: "WarmHost" }],
    })
    state.snapshot = { ...state.snapshot!, resyncVersion: 2 }
    shown.update()
    await act(async () => finish!(page))
    expect(await screen.findByText("WarmHost")).toBeVisible()
    expect(
      shown.client.getQueryCache().getAll()[0]?.options
    ).not.toHaveProperty("refetchInterval")
    const count = vi.mocked(getSystemEvents).mock.calls.length
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 50))
    })
    expect(getSystemEvents).toHaveBeenCalledTimes(count)
  })
  it.each([401, 403, 503])(
    "hides stale events and surfaces refresh failure HTTP %i",
    async (status) => {
      show()
      await screen.findByRole("table")
      await waitFor(() =>
        expect(
          screen.getByRole("button", { name: "Refresh system events" })
        ).toBeEnabled()
      )
      vi.mocked(getSystemEvents).mockRejectedValue(
        new ApiError(new Response(null, { status }), {
          title: "Events unavailable",
        })
      )
      fireEvent.click(
        screen.getByRole("button", { name: "Refresh system events" })
      )
      expect(await screen.findByRole("alert")).toHaveTextContent(
        "Events unavailable"
      )
      expect(screen.queryByRole("table")).not.toBeInTheDocument()
      expect(state.retry).toHaveBeenCalled()
    }
  )
})
