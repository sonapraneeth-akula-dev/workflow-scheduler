import type { ReactNode } from "react"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { beforeEach, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import { getTypes } from "@/features/catalog/api"
import { useLive } from "@/lib/live/useLive"
import { getServiceHistory, type ServiceSearch } from "./api"
import { ServiceHistory } from "./ServiceHistory"

vi.mock("./api", async (original) => ({
  ...(await original<typeof import("./api")>()),
  getServiceHistory: vi.fn(),
}))
vi.mock("@/features/catalog/api", async (original) => ({
  ...(await original<typeof import("@/features/catalog/api")>()),
  getTypes: vi.fn(),
}))
vi.mock("@/lib/live/useLive", () => ({
  useLive: vi.fn(() => ({ snapshot: undefined, retry: vi.fn() })),
}))
vi.mock("@tanstack/react-router", () => ({
  Link: ({
    to,
    params,
    children,
    ...props
  }: {
    to: string
    params?: Record<string, string>
    children: ReactNode
  }) => (
    <a
      {...props}
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
const search: ServiceSearch = { window: "7d", view: "cards", groupBy: "type" }
const response: Awaited<ReturnType<typeof getServiceHistory>> = {
  team: "team",
  type: null,
  window: "7d",
  from: "2026-10-12",
  to: "2026-10-19",
  nextCursor: null,
  items: ["build", "never"].map((name) => ({
    workflow: {
      id: `team/release/${name}`,
      team: "team",
      type: "release",
      name,
      activeVersion: null,
      latestVersion: {
        id: "019ed132-2f44-7000-8000-000000000001",
        semVer: "1.0.0",
      },
      metadata: { type: "release", values: {} },
      latestRun:
        name === "never"
          ? null
          : {
              id: "019ed132-2f44-7000-8000-000000000002",
              status: "Failed",
              createdAt: "2026-10-18T00:00:00Z",
              startedAt: null,
              endedAt: null,
            },
    },
    days: Array.from({ length: 7 }, (_, index) => ({
      day: `2026-10-${12 + index}`,
      counts:
        name === "never" ? {} : index === 6 ? { Succeeded: 2, Failed: 1 } : {},
    })),
  })),
}
function show() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  const change = vi.fn()
  const tree = (next = search) => (
    <QueryClientProvider client={client}>
      <ServiceHistory team="team" search={next} onChange={change} />
    </QueryClientProvider>
  )
  const rendered = render(tree())
  return {
    client,
    change,
    rerender: (next: ServiceSearch) => rendered.rerender(tree(next)),
  }
}
beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(getServiceHistory).mockResolvedValue(response)
  vi.mocked(getTypes).mockResolvedValue([
    {
      team: "team",
      name: "release",
      description: "Release",
      metadataSchema: { keys: [], allowFreeForm: true },
      latestStatusCounts: {},
    },
  ])
})

it("renders metadata-type containers with workflow names, actual status, last run, health and history", async () => {
  show()
  expect(await screen.findByRole("heading", { name: "release" })).toBeVisible()
  const card = screen.getByRole("heading", { name: "build" }).closest("li")!
  expect(within(card).getByText("Failed", { exact: true })).toBeVisible()
  expect(
    within(card).getByRole("link", { name: "History for team/release/build" })
  ).toHaveAttribute("href", "/t/team/release/w/build/history")
  expect(
    within(card).getByRole("link", {
      name: "View last run for team/release/build",
    })
  ).toHaveAttribute("href", "/runs/019ed132-2f44-7000-8000-000000000002")
  expect(card.querySelector("time")).toHaveAttribute(
    "datetime",
    "2026-10-18T00:00:00Z"
  )
  expect(card.querySelectorAll('[data-health-state="NoRuns"]')).toHaveLength(6)
  expect(card.querySelectorAll('[data-health-state="Failed"]')).toHaveLength(1)
  expect(within(card).getByRole("img")).toHaveAccessibleName(
    /3 runs, 2 succeeded, 1 other completed outcomes/
  )
  expect(screen.queryByText("Daily health details")).not.toBeInTheDocument()
  expect(card.querySelector("details")).toBeNull()
  const never = screen.getByRole("heading", { name: "never" }).closest("li")!
  expect(never.querySelectorAll('[data-health-state="NoRuns"]')).toHaveLength(7)
  expect(within(never).getByText("No completed runs")).toBeVisible()
  expect(
    within(never).getByRole("button", { name: "View last run" })
  ).toBeDisabled()
  expect(
    within(never).getByRole("link", { name: "History for team/release/never" })
  ).toBeVisible()
})

it("switches to the striped semantic table without changing the cached health query", async () => {
  const shown = show()
  await screen.findByText("build", { exact: true })
  shown.rerender({ ...search, view: "table" })
  const table = await screen.findByRole("table")
  expect(
    within(table)
      .getAllByRole("columnheader")
      .map((cell) => cell.textContent)
  ).toEqual(["Workflow", "Latest status", "Last run", "Health", "Actions"])
  expect(
    within(table).getByRole("link", {
      name: "View last run for team/release/build",
    })
  ).toHaveAttribute("href", "/runs/019ed132-2f44-7000-8000-000000000002")
  expect(
    within(table).getByRole("button", { name: "View last run" })
  ).toBeDisabled()
  expect(table.querySelector("details")).toBeNull()
  expect(within(table).getAllByRole("row")).toHaveLength(3)
  expect(table.querySelector("thead")).toHaveClass("bg-secondary")
  expect(table.querySelector("tbody tr")).toHaveClass(
    "odd:bg-card",
    "even:bg-muted/50"
  )
  expect(getServiceHistory).toHaveBeenCalledTimes(1)
})

it("changes type, window and view through the typed URL callback", async () => {
  const shown = show()
  await screen.findByText("build", { exact: true })
  await userEvent.selectOptions(
    screen.getByLabelText("Workflow type"),
    "release"
  )
  expect(shown.change).toHaveBeenLastCalledWith({ ...search, type: "release" })
  await userEvent.selectOptions(screen.getByLabelText("Health window"), "90d")
  expect(shown.change).toHaveBeenLastCalledWith({ ...search, window: "90d" })
  await userEvent.click(screen.getByRole("radio", { name: "Table format" }))
  expect(shown.change).toHaveBeenLastCalledWith({ ...search, view: "table" })
})

it("preserves cached cards on transient failure, retries, and hides all cached health on revoked access", async () => {
  const shown = show()
  await screen.findByText("build", { exact: true })
  vi.mocked(getServiceHistory).mockRejectedValue(
    new ApiError(new Response(null, { status: 503 }), {
      title: "Health unavailable",
    })
  )
  await shown.client.invalidateQueries({
    queryKey: ["catalog", "team", "team", "service-history"],
  })
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Health unavailable"
  )
  expect(screen.getByText("build", { exact: true })).toBeVisible()
  vi.mocked(getServiceHistory).mockResolvedValue(response)
  fireEvent.click(
    screen.getByRole("button", { name: "Refresh service history" })
  )
  await waitFor(() =>
    expect(screen.queryByRole("alert")).not.toBeInTheDocument()
  )
  vi.mocked(getServiceHistory).mockRejectedValue(
    new ApiError(new Response(null, { status: 403 }), {
      title: "Viewer revoked",
    })
  )
  await shown.client.invalidateQueries({
    queryKey: ["catalog", "team", "team", "service-history"],
  })
  expect(await screen.findByRole("alert")).toHaveTextContent("Viewer revoked")
  expect(screen.queryByText("build", { exact: true })).not.toBeInTheDocument()
  expect(screen.queryByRole("img")).not.toBeInTheDocument()
})

it("announces an empty catalogue without inventing healthy workflow cards", async () => {
  vi.mocked(getServiceHistory).mockResolvedValue({ ...response, items: [] })
  show()
  expect(
    await screen.findByText("No workflows are available in this team and type.")
  ).toBeVisible()
  expect(screen.queryByRole("img")).not.toBeInTheDocument()
})

it("discovers metadata keys from schemas and loaded workflows and updates only URL grouping", async () => {
  vi.mocked(getTypes).mockResolvedValue([
    {
      team: "team",
      name: "release",
      description: "Release",
      metadataSchema: {
        keys: [{ key: "environment", required: false, allowedValues: [] }],
        allowFreeForm: true,
      },
      latestStatusCounts: {},
    },
  ])
  vi.mocked(getServiceHistory).mockResolvedValue({
    ...response,
    items: response.items.map((entry) => ({
      ...entry,
      workflow: {
        ...entry.workflow,
        metadata: {
          type: "release",
          values: { component: "api", type: "custom-type" },
        },
      },
    })),
  })
  const shown = show()
  await screen.findByRole("heading", { name: "build" })
  const selector = screen.getByLabelText("Container grouping")
  expect(
    within(selector).getByRole("option", { name: "environment" })
  ).toBeVisible()
  expect(
    within(selector).getByRole("option", { name: "component" })
  ).toBeVisible()
  await userEvent.selectOptions(selector, "metadata:component")
  expect(shown.change).toHaveBeenLastCalledWith({
    ...search,
    groupBy: "metadata",
    groupKey: "component",
  })
  shown.rerender({ ...search, groupBy: "metadata", groupKey: "component" })
  expect(await screen.findByRole("region", { name: "api" })).toBeVisible()
  expect(getServiceHistory).toHaveBeenCalledTimes(1)
  shown.rerender({ ...search, groupBy: "metadata", groupKey: "type" })
  expect(
    await screen.findByRole("region", { name: "custom-type" })
  ).toBeVisible()
  await userEvent.selectOptions(selector, "type")
  expect(shown.change).toHaveBeenLastCalledWith({
    ...search,
    groupKey: undefined,
  })
})

it("keeps missing and empty metadata distinct and subscribes to every workflow type in mixed containers", async () => {
  const items = response.items.map((entry, index) => {
    const values: Record<string, string> = index ? { environment: "" } : {}
    return {
      ...entry,
      workflow: {
        ...entry.workflow,
        id: `team/${index ? "data" : "release"}/${entry.workflow.name}`,
        type: index ? "data" : "release",
        metadata: {
          type: index ? "data" : "release",
          values,
        },
      },
    }
  })
  vi.mocked(getServiceHistory).mockResolvedValue({ ...response, items })
  const shown = show()
  await screen.findByRole("heading", { name: "build" })
  shown.rerender({ ...search, groupBy: "metadata", groupKey: "environment" })
  expect(await screen.findByRole("region", { name: "Not set" })).toBeVisible()
  expect(screen.getByRole("region", { name: 'Empty value ("")' })).toBeVisible()
  vi.mocked(useLive).mockClear()
  shown.rerender({ ...search, groupBy: "metadata", groupKey: "constructor" })
  expect(await screen.findByRole("region", { name: "Not set" })).toBeVisible()
  expect(screen.getAllByRole("link", { name: /^History for / })).toHaveLength(2)
  expect(useLive).toHaveBeenCalledWith({ scope: "type", id: "team/release" })
  expect(useLive).toHaveBeenCalledWith({ scope: "type", id: "team/data" })
})

it("hides cached metadata grouping keys and containers after access is revoked", async () => {
  vi.mocked(getTypes).mockResolvedValue([
    {
      team: "team",
      name: "release",
      description: "Release",
      metadataSchema: {
        keys: [{ key: "private-key", required: false, allowedValues: [] }],
        allowFreeForm: true,
      },
      latestStatusCounts: {},
    },
  ])
  const shown = show()
  await screen.findByRole("option", { name: "private-key" })
  vi.mocked(getServiceHistory).mockRejectedValue(
    new ApiError(new Response(null, { status: 403 }), { title: "Revoked" })
  )
  await shown.client.invalidateQueries({
    queryKey: ["catalog", "team", "team", "service-history"],
  })
  expect(await screen.findByRole("alert")).toHaveTextContent("Revoked")
  expect(
    screen.queryByRole("option", { name: "private-key" })
  ).not.toBeInTheDocument()
  expect(
    screen.queryByRole("heading", { name: "build" })
  ).not.toBeInTheDocument()
})
