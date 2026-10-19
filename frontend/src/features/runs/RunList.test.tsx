import type { ReactNode } from "react"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { fireEvent, render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import { FilterBar } from "@/features/catalog/FilterBar"
import { getRuns, type RunRow } from "./api"
import { RunList } from "./RunList"
import { getTypes, getWorkflows } from "@/features/catalog/api"

vi.mock("./api", async (original) => ({
  ...(await original<typeof import("./api")>()),
  getRuns: vi.fn(),
}))
vi.mock("@/features/catalog/api", async (original) => ({
  ...(await original<typeof import("@/features/catalog/api")>()),
  getTypes: vi.fn(),
  getWorkflows: vi.fn(),
}))
vi.mock("@tanstack/react-router", () => ({
  Link: ({
    params,
    children,
    ...props
  }: {
    params: { id: string }
    children: ReactNode
  }) => (
    <a {...props} href={`/runs/${params.id}`}>
      {children}
    </a>
  ),
}))
const row: RunRow = {
  id: "019ed132-2f44-7000-8000-000000000010",
  workflowId: "team/release/build",
  versionId: "019ed132-2f44-7000-8000-000000000001",
  seriesId: null,
  team: "team",
  type: "release",
  status: "Running",
  createdAt: "2026-10-16T07:00:00Z",
  startedAt: null,
  endedAt: null,
  metadata: { type: "release", values: { environment: "original" } },
}
function show(element: ReactNode) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  return {
    ...render(element, {
      wrapper: ({ children }: { children: ReactNode }) => (
        <QueryClientProvider client={client}>{children}</QueryClientProvider>
      ),
    }),
    client,
  }
}

describe("filtered run list", () => {
  beforeEach(() => vi.clearAllMocks())

  it("bounds rendered DOM while scrolling 10,000 loaded runs", async () => {
    const rows = Array.from({ length: 10_000 }, (_, index) => ({
      ...row,
      id: `019ed132-2f44-7000-8000-${String(10000 - index).padStart(12, "0")}`,
    }))
    vi.mocked(getRuns).mockResolvedValue({ items: rows, nextCursor: null })
    const { container } = show(<RunList search={{}} />)
    await screen.findByText("10000 loaded runs, newest first.")
    expect(container.querySelectorAll("[data-run-row]")).toHaveLength(10)
    const scroll = screen.getByRole("region", {
      name: "Scrollable run results",
    })
    fireEvent.scroll(scroll, { target: { scrollTop: 1000 * 160 } })
    expect(container.querySelectorAll("[data-run-row]")).toHaveLength(10)
    expect(container.querySelectorAll("[data-run-row]")[0]).toHaveAttribute(
      "aria-posinset",
      "998"
    )
    fireEvent.scroll(scroll, { target: { scrollTop: 9999 * 160 } })
    expect(screen.getAllByRole("link")[0]).toHaveAttribute(
      "href",
      `/runs/${rows.at(-4)!.id}`
    )
  })

  it("offers a complete loaded-list alternative without changing requested data", async () => {
    const rows = Array.from({ length: 20 }, (_, index) => ({
      ...row,
      id: String(index),
    }))
    vi.mocked(getRuns).mockResolvedValue({ items: rows, nextCursor: null })
    const { container } = show(<RunList search={{}} />)
    await screen.findByText("20 loaded runs, newest first.")
    fireEvent.click(
      screen.getByRole("button", { name: "Use accessible full list" })
    )
    expect(container.querySelectorAll("[data-run-row]")).toHaveLength(20)
    fireEvent.click(
      screen.getByRole("button", { name: "Use virtualized list" })
    )
    expect(
      container.querySelectorAll("[data-run-row]").length
    ).toBeLessThanOrEqual(10)
  })

  it("groups only loaded historical values, preserving missing versus empty and unknown labels", async () => {
    vi.mocked(getRuns).mockResolvedValue({
      items: [
        { ...row, id: "a", metadata: null },
        {
          ...row,
          id: "b",
          metadata: { type: "release", values: { environment: "" } },
        },
        {
          ...row,
          id: "c",
          metadata: { type: "release", values: { environment: "prod" } },
        },
      ],
      nextCursor: null,
    })
    show(<RunList search={{ groupBy: "metadata", groupKey: "environment" }} />)
    await screen.findByText("environment: prod")
    expect(screen.getByText("environment: (not recorded)")).toBeVisible()
    expect(screen.getByText("environment: (empty)")).toBeVisible()
  })

  it("loads subsequent pages explicitly and reuses the server query when only grouping changes", async () => {
    const user = userEvent.setup()
    vi.mocked(getRuns)
      .mockResolvedValueOnce({ items: [row], nextCursor: "next" })
      .mockResolvedValueOnce({
        items: [{ ...row, id: "019ed132-2f44-7000-8000-000000000009" }],
        nextCursor: null,
      })
    const { rerender } = show(<RunList search={{ team: "team" }} />)
    await user.click(
      await screen.findByRole("button", { name: "Load more runs" })
    )
    await screen.findByText("2 loaded runs, newest first.")
    expect(getRuns).toHaveBeenLastCalledWith(
      { team: "team" },
      { cursor: "next", afterId: row.id },
      expect.any(AbortSignal)
    )
    rerender(<RunList search={{ team: "team", groupBy: "status" }} />)
    await screen.findByText(
      "2 loaded runs, newest first within each loaded group."
    )
    expect(getRuns).toHaveBeenCalledTimes(2)
  })

  it("hides cached results on revoked access and exposes retry without automatic polling", async () => {
    vi.mocked(getRuns).mockResolvedValue({ items: [row], nextCursor: null })
    const { client } = show(<RunList search={{}} />)
    await screen.findByRole("link")
    vi.mocked(getRuns).mockRejectedValue(
      new ApiError(new Response(null, { status: 403 }), {
        title: "Viewer access revoked.",
      })
    )
    await client.invalidateQueries({ queryKey: ["runs"] })
    await screen.findByText("Viewer access revoked.")
    expect(screen.queryByRole("link")).not.toBeInTheDocument()
    expect(screen.getByRole("button", { name: "Refresh runs" })).toBeEnabled()
    expect(getRuns).toHaveBeenCalledTimes(2)
  })
})

describe("run filter form", () => {
  beforeEach(() => {
    vi.mocked(getTypes).mockResolvedValue([])
    vi.mocked(getWorkflows).mockResolvedValue({ items: [], nextCursor: null })
  })
  it("keeps its route team when searching and clearing filters", async () => {
    const change = vi.fn()
    show(
      <FilterBar
        team="team"
        filters={{ team: "team", status: "Failed" }}
        onChange={change}
      />
    )
    expect(screen.queryByLabelText("Team")).not.toBeInTheDocument()
    fireEvent.submit(screen.getByRole("form", { name: "Run filters" }))
    expect(change).toHaveBeenLastCalledWith({
      team: "team",
      status: "Failed",
      groupBy: "none",
    })
    await userEvent.click(screen.getByRole("button", { name: "Clear filters" }))
    expect(change).toHaveBeenLastCalledWith({ team: "team" })
  })
  it("submits keyboard-entered exact filters and grouping with a clear reset", async () => {
    const user = userEvent.setup()
    const change = vi.fn()
    show(<FilterBar filters={{}} onChange={change} />)
    await user.type(screen.getByLabelText("Team"), "team")
    await user.type(screen.getByLabelText("Type"), "release")
    await user.selectOptions(screen.getByLabelText("Run status"), "Running")
    await user.click(screen.getByText("Advanced filters", { exact: true }))
    await user.selectOptions(
      screen.getByLabelText("Group loaded runs by"),
      "metadata"
    )
    await user.type(screen.getByLabelText("Metadata group key"), "environment")
    fireEvent.change(screen.getByLabelText("Metadata filters (JSON)"), {
      target: { value: '{"environment":" exact "}' },
    })
    await user.click(screen.getByRole("button", { name: "Search runs" }))
    expect(change).toHaveBeenCalledWith(
      expect.objectContaining({
        team: "team",
        type: "release",
        status: "Running",
        metadata: { environment: " exact " },
        groupBy: "metadata",
        groupKey: "environment",
      })
    )
    await user.click(screen.getByRole("button", { name: "Clear filters" }))
    expect(change).toHaveBeenLastCalledWith({})
  })

  it("blocks malformed metadata and contradictory type filters without a request", async () => {
    const change = vi.fn()
    show(<FilterBar filters={{ type: "release" }} onChange={change} />)
    fireEvent.submit(screen.getByRole("form", { name: "Run filters" }))
    await waitFor(() =>
      expect(screen.getByRole("alert")).toHaveTextContent("requires a team")
    )
    fireEvent.change(screen.getByLabelText("Metadata filters (JSON)"), {
      target: { value: "{" },
    })
    fireEvent.submit(screen.getByRole("form", { name: "Run filters" }))
    expect(screen.getByRole("alert")).toHaveTextContent("JSON object")
    expect(change).not.toHaveBeenCalled()
  })
  it("opens copied advanced filters without hiding their active state", () => {
    show(
      <FilterBar
        team="team"
        filters={{
          team: "team",
          metadata: { environment: "dev" },
          seriesId: "019ed132-2f44-7000-8000-000000000001",
        }}
        onChange={vi.fn()}
      />
    )
    expect(
      screen.getByText("Advanced filters (2 active)").closest("details")
    ).toHaveAttribute("open")
    expect(screen.getByLabelText("Series ID")).toBeVisible()
    expect(screen.getByLabelText("Metadata filters (JSON)")).toHaveValue(
      '{"environment":"dev"}'
    )
  })
  it("suggests authorized values, builds exact metadata filters and still accepts custom entries", async () => {
    vi.mocked(getTypes).mockResolvedValue([
      {
        team: "team",
        name: "release",
        description: "Releases",
        latestStatusCounts: {},
        metadataSchema: {
          keys: [
            {
              key: "environment",
              required: true,
              allowedValues: ["dev", "prod"],
            },
          ],
          allowFreeForm: true,
        },
      },
    ])
    vi.mocked(getWorkflows).mockResolvedValue({
      items: [
        {
          id: "team/release/build",
          team: "team",
          type: "release",
          name: "build",
          activeVersion: null,
          latestVersion: { id: row.versionId, semVer: "1.0.0" },
          metadata: {
            type: "release",
            values: { component: "api", environment: "dev" },
          },
          latestRun: null,
        },
      ],
      nextCursor: null,
    })
    const change = vi.fn()
    show(
      <FilterBar
        team="team"
        filters={{ team: "team", type: "release" }}
        onChange={change}
      />
    )
    await waitFor(() =>
      expect(
        document.querySelector('datalist option[value="team/release/build"]')
      ).toBeInTheDocument()
    )
    expect(
      document.querySelector('datalist option[value="release"]')
    ).toBeInTheDocument()
    await userEvent.click(screen.getByText("Advanced filters", { exact: true }))
    fireEvent.change(screen.getByLabelText("Metadata key", { exact: true }), {
      target: { value: "environment" },
    })
    expect(
      document.querySelector('datalist option[value="prod"]')
    ).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText("Metadata value"), {
      target: { value: " custom value " },
    })
    await userEvent.click(
      screen.getByRole("button", { name: "Add metadata filter" })
    )
    expect(screen.getByLabelText("Metadata filters (JSON)")).toHaveValue(
      '{"environment":" custom value "}'
    )
    fireEvent.change(screen.getByLabelText("Type", { exact: true }), {
      target: { value: "custom-type" },
    })
    fireEvent.change(screen.getByLabelText("Workflow ID"), {
      target: { value: "team/custom-type/custom-workflow" },
    })
    await userEvent.click(screen.getByRole("button", { name: "Search runs" }))
    expect(change).toHaveBeenLastCalledWith(
      expect.objectContaining({
        team: "team",
        type: "custom-type",
        workflowId: "team/custom-type/custom-workflow",
        metadata: { environment: " custom value " },
      })
    )
  })
  it("surfaces suggestion failure, hides revoked cached options and retries the failing query", async () => {
    vi.mocked(getTypes).mockResolvedValue([
      {
        team: "team",
        name: "release",
        description: "",
        latestStatusCounts: {},
        metadataSchema: { keys: [], allowFreeForm: true },
      },
    ])
    const { client } = show(
      <FilterBar
        team="team"
        filters={{ team: "team", type: "release" }}
        onChange={vi.fn()}
      />
    )
    await waitFor(() =>
      expect(
        document.querySelector('datalist option[value="release"]')
      ).toBeInTheDocument()
    )
    vi.mocked(getTypes).mockRejectedValue(
      new ApiError(new Response(null, { status: 403 }), {
        title: "Suggestions revoked",
      })
    )
    await client.invalidateQueries({
      queryKey: ["catalog", "team", "team", "filter-types"],
    })
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Suggestions revoked"
    )
    expect(
      document.querySelector('datalist option[value="release"]')
    ).not.toBeInTheDocument()
    vi.mocked(getTypes).mockResolvedValue([])
    await userEvent.click(
      screen.getByRole("button", { name: "Retry filter suggestions" })
    )
    await waitFor(() =>
      expect(screen.queryByRole("alert")).not.toBeInTheDocument()
    )
  })
})
