import type { ReactNode } from "react"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { fireEvent, render, screen, waitFor } from "@testing-library/react"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import { useLive } from "@/lib/live/useLive"
import {
  getTeams,
  getTypes,
  getWorkflows,
  runStatuses,
  type CatalogWorkflow,
} from "./api"
import { TeamList } from "./TeamList"
import { TeamArea } from "./TeamArea"
import { TypeWorkflows } from "./TypeWorkflows"
import { StatusBadge } from "@/components/StatusBadge"

vi.mock("./api", async (original) => ({
  ...(await original<typeof import("./api")>()),
  getTeams: vi.fn(),
  getTypes: vi.fn(),
  getWorkflows: vi.fn(),
}))
vi.mock("@/lib/live/useLive", () => ({ useLive: vi.fn() }))
vi.mock("@tanstack/react-router", () => ({
  Link: ({
    to,
    params,
    children,
    className,
  }: {
    to: string
    params?: Record<string, string>
    children: ReactNode
    className?: string
  }) => (
    <a
      className={className}
      href={Object.entries(params ?? {}).reduce(
        (path, [key, value]) =>
          path.replace(`$${key}`, encodeURIComponent(value)),
        to
      )}
    >
      {children}
    </a>
  ),
}))

const version = { id: "019ed132-2f44-7000-8000-000000000001", semVer: "2.0.0" }
const workflow: CatalogWorkflow = {
  id: "team/release/build",
  team: "team",
  type: "release",
  name: "build",
  activeVersion: { ...version, semVer: "1.0.0" },
  latestVersion: version,
  metadata: { type: "release", values: {} },
  latestRun: null,
}
const live = {
  resource: { scope: "type", id: "team/release" },
  status: "live",
  error: null,
  delta: null,
  resyncVersion: 0,
  reportVersion: 0,
} as const
const retry = vi.fn()

function show(
  element: ReactNode,
  client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
) {
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  )
  return { ...render(element, { wrapper }), client }
}

describe("catalogue views", () => {
  beforeEach(() => {
    vi.resetAllMocks()
    vi.mocked(getTeams).mockResolvedValue([
      { team: "team", typeCount: 1, presentation: null },
    ])
    vi.mocked(useLive).mockReturnValue({ snapshot: live, retry })
  })

  it("announces loading then renders permitted team navigation", async () => {
    vi.mocked(getTeams).mockResolvedValue([
      { team: "team", typeCount: 1, presentation: null },
    ])
    show(<TeamList />)
    expect(screen.getByText("Loading teams...")).toBeInTheDocument()
    expect(await screen.findByRole("link", { name: "team" })).toHaveAttribute(
      "href",
      "/t/team"
    )
    expect(screen.getByText("1 permitted type")).toBeInTheDocument()
  })

  it("distinguishes an empty team catalogue from failed access", async () => {
    vi.mocked(getTeams).mockResolvedValue([])
    show(<TeamList />)
    expect(
      await screen.findByText("No teams are available to your account.")
    ).toBeInTheDocument()
  })

  it("links team run choices to their own scoped listing", async () => {
    show(<TeamList runSearch={{ status: "Failed" }} />)
    expect(await screen.findByRole("link", { name: "team" })).toHaveAttribute(
      "href",
      "/t/team/runs"
    )
  })

  it("preserves cached rows for transient errors but hides them after access denial", async () => {
    vi.mocked(getTeams).mockResolvedValue([
      { team: "team", typeCount: 1, presentation: null },
    ])
    show(<TeamList />)
    await screen.findByRole("link", { name: "team" })
    vi.mocked(getTeams).mockRejectedValueOnce(new Error("Offline"))
    fireEvent.click(screen.getByRole("button", { name: "Refresh teams" }))
    expect(await screen.findByRole("alert")).toHaveTextContent("Offline")
    expect(screen.getByRole("link", { name: "team" })).toBeInTheDocument()
    vi.mocked(getTeams).mockRejectedValueOnce(
      new ApiError(new Response(null, { status: 403 }), { title: "Denied" })
    )
    fireEvent.click(screen.getByRole("button", { name: "Refresh teams" }))
    await waitFor(() =>
      expect(
        screen.queryByRole("link", { name: "team" })
      ).not.toBeInTheDocument()
    )
    expect(screen.getByRole("alert")).toHaveTextContent("Denied")
  })

  it("renders type descriptions, every latest-status count and type-scoped live subscriptions", async () => {
    vi.mocked(getTypes).mockResolvedValue([
      {
        team: "team",
        name: "release",
        description: "Deploy releases",
        latestStatusCounts: { Failed: 2, NeverRun: 1 },
      },
    ])
    show(<TeamArea team="team" />)
    expect(
      await screen.findByRole("link", { name: "team / release" })
    ).toHaveAttribute("href", "/t/team/release")
    expect(screen.getByText("Deploy releases")).toBeInTheDocument()
    expect(screen.getByText("Failed").parentElement).toHaveTextContent("2")
    expect(screen.getByText("Never run").parentElement).toHaveTextContent("1")
    expect(useLive).toHaveBeenCalledWith({ scope: "type", id: "team/release" })
  })

  it("disambiguates identical type names with each owning team and preserves exact routes", async () => {
    vi.mocked(getTeams).mockResolvedValue([
      { team: "teamA", typeCount: 1, presentation: null },
      { team: "teamB", typeCount: 1, presentation: null },
    ])
    vi.mocked(getTypes).mockImplementation(async (team) => [
      {
        team,
        name: "perf",
        description: "Performance workflows",
        latestStatusCounts: {},
      },
    ])
    show(
      <>
        <TeamArea team="teamA" />
        <TeamArea team="teamB" />
      </>
    )
    expect(
      await screen.findByRole("link", { name: "teamA / perf" })
    ).toHaveAttribute("href", "/t/teamA/perf")
    expect(
      await screen.findByRole("link", { name: "teamB / perf" })
    ).toHaveAttribute("href", "/t/teamB/perf")
    expect(useLive).toHaveBeenCalledWith({ scope: "type", id: "teamA/perf" })
    expect(useLive).toHaveBeenCalledWith({ scope: "type", id: "teamB/perf" })
  })

  it("renders empty types without claiming workflow failures", async () => {
    vi.mocked(getTypes).mockResolvedValue([])
    show(<TeamArea team="team" />)
    expect(
      await screen.findByText("No permitted types in this team.")
    ).toBeInTheDocument()
  })

  it("applies type order and server-side default filters to team counts", async () => {
    vi.mocked(getTeams).mockResolvedValue([
      {
        team: "team",
        typeCount: 2,
        presentation: {
          typeOrder: ["perf", "release"],
          groupingKeys: [],
          listColumns: [],
          defaultFilters: { environment: "dev" },
          labels: { environment: "Environment" },
        },
      },
    ])
    vi.mocked(getTypes).mockResolvedValue([
      {
        team: "team",
        name: "release",
        description: "",
        latestStatusCounts: {},
      },
      { team: "team", name: "perf", description: "", latestStatusCounts: {} },
    ])
    show(<TeamArea team="team" />)
    await screen.findByRole("link", { name: "team / perf" })
    const links = screen.getAllByRole("link").map((link) => link.textContent)
    expect(links).toEqual([
      "Teams",
      "Search runs in this team",
      "team / perf",
      "team / release",
    ])
    expect(getTypes).toHaveBeenCalledWith(
      "team",
      expect.any(AbortSignal),
      {},
      true
    )
    expect(
      screen.getByRole("region", { name: "Default metadata filters" })
    ).toHaveTextContent("Environment:dev")
  })

  it("applies metadata columns, labels and tuple grouping to filtered workflow pages", async () => {
    vi.mocked(getTeams).mockResolvedValue([
      {
        team: "team",
        typeCount: 1,
        presentation: {
          typeOrder: [],
          groupingKeys: ["environment"],
          listColumns: ["component"],
          defaultFilters: { environment: "dev" },
          labels: { environment: "Environment", component: "Component" },
        },
      },
    ])
    vi.mocked(getWorkflows).mockResolvedValue({
      items: [
        {
          ...workflow,
          metadata: {
            type: "release",
            values: { environment: "dev", component: "api" },
          },
        },
      ],
      nextCursor: null,
    })
    show(<TypeWorkflows team="team" type="release" />)
    const table = await screen.findByRole("table")
    expect(table).toHaveTextContent("Environment: dev")
    expect(
      screen.getByRole("columnheader", { name: "Component" })
    ).toBeInTheDocument()
    expect(screen.getByRole("cell", { name: "api" })).toBeInTheDocument()
    expect(getWorkflows).toHaveBeenCalledWith(
      "team",
      "release",
      null,
      expect.any(AbortSignal),
      {},
      true
    )
  })

  it("paginates workflows, preserves active/latest versions, and labels never-run workflows", async () => {
    vi.mocked(getWorkflows)
      .mockResolvedValueOnce({ items: [workflow], nextCursor: "next" })
      .mockResolvedValueOnce({
        items: [
          {
            ...workflow,
            id: "team/release/other",
            name: "other",
            activeVersion: null,
          },
        ],
        nextCursor: null,
      })
    show(<TypeWorkflows team="team" type="release" />)
    expect(await screen.findByRole("table")).toHaveTextContent("1.0.0")
    expect(screen.getByRole("table")).toHaveTextContent("2.0.0")
    expect(screen.getByText("Never run")).toBeInTheDocument()
    fireEvent.click(screen.getByRole("button", { name: "Load more workflows" }))
    expect(await screen.findByText("No active version")).toBeInTheDocument()
    expect(getWorkflows).toHaveBeenLastCalledWith(
      "team",
      "release",
      "next",
      expect.any(AbortSignal),
      {},
      true
    )
    expect(screen.getAllByRole("row")).toHaveLength(3)
    expect(
      screen.queryByRole("button", { name: "Load more workflows" })
    ).not.toBeInTheDocument()
  })

  it("retains loaded workflows when another page fails and refresh recovers", async () => {
    vi.mocked(getWorkflows)
      .mockResolvedValueOnce({ items: [workflow], nextCursor: "next" })
      .mockRejectedValueOnce(new Error("Next page failed"))
    show(<TypeWorkflows team="team" type="release" />)
    await screen.findByRole("table")
    fireEvent.click(screen.getByRole("button", { name: "Load more workflows" }))
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Next page failed"
    )
    expect(screen.getByRole("table")).toHaveTextContent("build")
    vi.mocked(getWorkflows).mockResolvedValue({ items: [], nextCursor: null })
    fireEvent.click(screen.getByRole("button", { name: "Refresh workflows" }))
    expect(
      await screen.findByText("No workflows in this type.")
    ).toBeInTheDocument()
  })

  it("hides cached workflows after scoped access revocation", async () => {
    vi.mocked(getWorkflows)
      .mockResolvedValueOnce({ items: [workflow], nextCursor: null })
      .mockRejectedValueOnce(
        new ApiError(new Response(null, { status: 403 }), { title: "Revoked" })
      )
    show(<TypeWorkflows team="team" type="release" />)
    await screen.findByRole("table")
    fireEvent.click(screen.getByRole("button", { name: "Refresh workflows" }))
    await waitFor(() =>
      expect(screen.queryByRole("table")).not.toBeInTheDocument()
    )
    expect(screen.getByRole("alert")).toHaveTextContent("Revoked")
  })

  it("invalidates the active type and counts on live changes, without invalidating other teams or types", async () => {
    vi.mocked(getWorkflows).mockResolvedValue({
      items: [workflow],
      nextCursor: null,
    })
    const view = show(<TypeWorkflows team="team" type="release" />)
    await screen.findByRole("table")
    view.client.setQueryData(["catalog", "team", "team", "types"], [])
    view.client.setQueryData(
      ["catalog", "team", "team", "workflows", "other"],
      "other"
    )
    view.client.setQueryData(["catalog", "team", "foreign", "types"], [])
    vi.mocked(useLive).mockReturnValue({
      snapshot: {
        ...live,
        delta: { scope: "type", id: "team/release", items: [] },
      },
      retry,
    })
    view.rerender(<TypeWorkflows team="team" type="release" />)
    await waitFor(() => expect(getWorkflows).toHaveBeenCalledTimes(2))
    expect(
      view.client.getQueryState(["catalog", "team", "team", "types"])
        ?.isInvalidated
    ).toBe(true)
    expect(
      view.client.getQueryState([
        "catalog",
        "team",
        "team",
        "workflows",
        "other",
      ])?.isInvalidated
    ).toBe(false)
    expect(
      view.client.getQueryState(["catalog", "team", "foreign", "types"])
        ?.isInvalidated
    ).toBe(false)
  })

  it("resyncs after a reconnect and explicitly offers live retry after failure", async () => {
    vi.mocked(getWorkflows).mockResolvedValue({ items: [], nextCursor: null })
    const view = show(<TypeWorkflows team="team" type="release" />)
    await screen.findByText("No workflows in this type.")
    vi.mocked(useLive).mockReturnValue({
      snapshot: { ...live, resyncVersion: 1 },
      retry,
    })
    view.rerender(<TypeWorkflows team="team" type="release" />)
    await waitFor(() => expect(getWorkflows).toHaveBeenCalledTimes(2))
    vi.mocked(useLive).mockReturnValue({
      snapshot: {
        ...live,
        status: "error",
        error: new Error("Disconnected"),
      },
      retry,
    })
    view.rerender(<TypeWorkflows team="team" type="release" />)
    expect(
      screen.getByText("Live updates unavailable. Refresh workflows or retry.")
    ).toBeInTheDocument()
    fireEvent.click(screen.getByRole("button", { name: "Retry live updates" }))
    expect(retry).toHaveBeenCalledOnce()
  })

  it.each([...runStatuses, "NeverRun"] as const)(
    "exposes readable %s status independent of color",
    (status) => {
      show(<StatusBadge status={status} />)
      const badge = screen.getByText(
        status === "NeverRun" ? "Never run" : status
      )
      expect(badge).toBeInTheDocument()
      expect(badge.querySelector("svg")).toHaveAttribute("aria-hidden", "true")
    }
  )
})
