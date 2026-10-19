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
import { getTeams, getTypes } from "@/features/catalog/api"
import { useLive } from "@/lib/live/useLive"
import type { LiveSnapshot } from "@/lib/live/subscriptions"
import { getServiceHistory, type ServiceSearch } from "./api"
import { ServiceHistory } from "./ServiceHistory"
import {
  ServiceHistoryPreferences,
  profilePreferenceKey,
} from "@/lib/ServiceHistoryPreferences"

vi.mock("./api", async (original) => ({
  ...(await original<typeof import("./api")>()),
  getServiceHistory: vi.fn(),
}))
vi.mock("@/features/catalog/api", async (original) => ({
  ...(await original<typeof import("@/features/catalog/api")>()),
  getTypes: vi.fn(),
  getTeams: vi.fn(),
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
const search: ServiceSearch = { view: "cards", groupBy: "type" }
const session = { displayName: "Local developer", isDevelopment: true }
const response: Awaited<ReturnType<typeof getServiceHistory>> = {
  team: "team",
  type: null,
  runLimit: 10,
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
    runs:
      name === "never"
        ? []
        : (["Failed", "Succeeded", "Succeeded"] as const).map(
            (status, index) => ({
              id: `019ed132-2f44-7000-8000-${String(2 - index).padStart(12, "0")}`,
              status,
              createdAt: `2026-10-18T00:00:0${2 - index}Z`,
              startedAt: null,
              endedAt: null,
            })
          ),
  })),
}
function show() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  const change = vi.fn()
  const tree = (next = search) => (
    <QueryClientProvider client={client}>
      <ServiceHistoryPreferences session={session}>
        <ServiceHistory team="team" search={next} onChange={change} />
      </ServiceHistoryPreferences>
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
  vi.mocked(useLive).mockReturnValue({
    snapshot: null,
    retry: vi.fn(),
  })
  localStorage.clear()
  vi.mocked(getTeams).mockResolvedValue([
    { team: "team", typeCount: 1, presentation: null },
  ])
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

it("renders individual run outcomes with exact new-tab links, last run and full history", async () => {
  show()
  expect(await screen.findByRole("heading", { name: "Release" })).toBeVisible()
  const card = screen.getByRole("heading", { name: "Build" }).closest("li")!
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
  const lastRunLabel = within(card).getByText("Last run:", { exact: true })
  expect(lastRunLabel.tagName).toBe("SPAN")
  expect(card.querySelector("time")?.parentElement).toBe(
    lastRunLabel.parentElement
  )
  expect(lastRunLabel.parentElement?.tagName).toBe("P")
  expect(card.querySelectorAll('[data-run-status="Succeeded"]')).toHaveLength(2)
  expect(card.querySelectorAll('[data-run-status="Failed"]')).toHaveLength(1)
  expect(within(card).getByRole("group")).toHaveAccessibleName(
    "Run history: 3 recent runs, newest first"
  )
  const runLinks = within(card).getAllByRole("link", {
    name: /Opens in a new tab/,
  })
  expect(runLinks).toHaveLength(3)
  runLinks.forEach((link, index) => {
    expect(link).toHaveAttribute(
      "href",
      `/runs/${response.items[0]!.runs[index]!.id}`
    )
    expect(link).toHaveAttribute("target", "_blank")
    expect(link).toHaveAttribute("rel", "noopener noreferrer")
    expect(link).toHaveAccessibleName(
      new RegExp(response.items[0]!.runs[index]!.status)
    )
  })
  expect(within(card).getByText("66.7% completed successfully")).toBeVisible()
  expect(screen.queryByText("Daily health details")).not.toBeInTheDocument()
  expect(card.querySelector("details")).toBeNull()
  const never = screen.getByRole("heading", { name: "Never" }).closest("li")!
  expect(
    within(never).getByText("Last run:", { exact: true }).parentElement
  ).toHaveTextContent("Last run: Never run")
  expect(never.querySelectorAll("[data-run-id]")).toHaveLength(0)
  expect(within(never).getByText("No runs", { exact: true })).toBeVisible()
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
  await screen.findByText("Build", { exact: true })
  shown.rerender({ ...search, view: "table" })
  const table = await screen.findByRole("table")
  expect(
    within(table)
      .getAllByRole("columnheader")
      .map((cell) => cell.textContent)
  ).toEqual([
    "Workflow",
    "Latest status",
    "Last run",
    "Run instances",
    "Actions",
  ])
  expect(
    within(table).getByRole("link", {
      name: "View last run for team/release/build",
    })
  ).toHaveAttribute("href", "/runs/019ed132-2f44-7000-8000-000000000002")
  expect(
    within(table).getByRole("button", { name: "View last run" })
  ).toBeDisabled()
  expect(table.querySelector("details")).toBeNull()
  expect(
    table.querySelector("tbody tr")!.querySelectorAll("[data-run-id]")
  ).toHaveLength(3)
  expect(within(table).getAllByRole("row")).toHaveLength(3)
  expect(table.querySelector("thead")).toHaveClass("bg-secondary")
  expect(table.querySelector("tbody tr")).toHaveClass(
    "odd:bg-card",
    "even:bg-muted/50"
  )
  expect(getServiceHistory).toHaveBeenCalledTimes(1)
})

it("uses the personal default run count while changing type and view through the URL callback", async () => {
  const shown = show()
  await screen.findByText("Build", { exact: true })
  await userEvent.selectOptions(
    screen.getByLabelText("Workflow type"),
    "release"
  )
  expect(shown.change).toHaveBeenLastCalledWith({ ...search, type: "release" })
  expect(screen.queryByLabelText("Health window")).not.toBeInTheDocument()
  expect(
    screen.getByText("Run instances: latest 10 runs per workflow")
  ).toBeVisible()
  expect(getServiceHistory).toHaveBeenCalledWith(
    "team",
    undefined,
    10,
    null,
    expect.any(AbortSignal)
  )
  await userEvent.click(screen.getByRole("radio", { name: "Table format" }))
  expect(shown.change).toHaveBeenLastCalledWith({ ...search, view: "table" })
})

it("uses a saved personal run limit and calculates success from the shown instances", async () => {
  localStorage.setItem(profilePreferenceKey(session)!, "25")
  const entry = response.items[0]!
  vi.mocked(getServiceHistory).mockResolvedValue({
    ...response,
    runLimit: 25,
    items: [
      {
        ...entry,
        runs: Array.from({ length: 25 }, (_, index) => ({
          ...entry.runs[0]!,
          id: `019ed132-2f44-7000-8000-${(100 - index).toString(16).padStart(12, "0")}`,
          status: index < 20 ? "Succeeded" : "Failed",
        })),
      },
    ],
  })
  show()
  expect(await screen.findByText("Latest 25 runs / up to 25")).toBeVisible()
  expect(
    screen.getAllByRole("link", { name: /Opens in a new tab/ })
  ).toHaveLength(25)
  expect(screen.getByText("80% completed successfully")).toBeVisible()
  expect(getServiceHistory).toHaveBeenCalledWith(
    "team",
    undefined,
    25,
    null,
    expect.any(AbortSignal)
  )
})

it("distinguishes every outcome with an accessible status icon and matching color", async () => {
  const entry = response.items[0]!
  const statuses = [
    "Queued",
    "Running",
    "Aborting",
    "Succeeded",
    "Failed",
    "TimedOut",
    "Aborted",
  ] as const
  vi.mocked(getServiceHistory).mockResolvedValue({
    ...response,
    items: [
      {
        ...entry,
        runs: statuses.map((status, index) => ({
          ...entry.runs[0]!,
          id: `019ed132-2f44-7000-8000-${(100 - index).toString(16).padStart(12, "0")}`,
          status,
        })),
      },
    ],
  })
  show()
  await screen.findByRole("heading", { name: "Build" })
  const colors = [
    "bg-info",
    "bg-info",
    "bg-info",
    "bg-success",
    "bg-destructive",
    "bg-destructive",
    "bg-warning",
  ]
  screen
    .getAllByRole("link", { name: /Opens in a new tab/ })
    .forEach((link, index) => {
      expect(link).toHaveAccessibleName(new RegExp(`^${statuses[index]} run `))
      expect(link).toHaveClass(colors[index]!, "size-6")
      expect(link.querySelector("svg")).toHaveAttribute("aria-hidden", "true")
    })
})

it("preserves cached cards on transient failure, retries, and hides all cached health on revoked access", async () => {
  const shown = show()
  await screen.findByText("Build", { exact: true })
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
  expect(screen.getByText("Build", { exact: true })).toBeVisible()
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
  expect(screen.queryByText("Build", { exact: true })).not.toBeInTheDocument()
  expect(screen.queryByRole("img")).not.toBeInTheDocument()
})

it("manually refreshes run history, workflow types and display-name configuration", async () => {
  const shown = show()
  await screen.findByText("Build", { exact: true })
  await waitFor(() => expect(shown.client.isFetching()).toBe(0))
  await userEvent.click(
    screen.getByRole("button", { name: "Refresh service history" })
  )
  await waitFor(() => {
    expect(getServiceHistory).toHaveBeenCalledTimes(2)
    expect(getTypes).toHaveBeenCalledTimes(2)
    expect(getTeams).toHaveBeenCalledTimes(2)
  })
})

it("automatically refreshes on run updates and reconnect resync, with a separate retry for failed live updates", async () => {
  const retry = vi.fn()
  let snapshot: LiveSnapshot = {
    resource: { scope: "type", id: "team/release" },
    status: "live",
    error: null,
    delta: null,
    resyncVersion: 0,
    reportVersion: 0,
  }
  vi.mocked(useLive).mockImplementation(() => ({ snapshot, retry }))
  const shown = show()
  await screen.findByText("Build", { exact: true })
  await waitFor(() => expect(shown.client.isFetching()).toBe(0))
  const updated = structuredClone(response)
  updated.items[0]!.runs[0]!.status = "Succeeded"
  vi.mocked(getServiceHistory).mockResolvedValue(updated)
  snapshot = {
    ...snapshot,
    delta: { scope: "type", id: "team/release", items: [] },
  }
  shown.rerender(search)
  expect(await screen.findByText("100% completed successfully")).toBeVisible()
  await waitFor(() => expect(shown.client.isFetching()).toBe(0))
  const calls = vi.mocked(getServiceHistory).mock.calls.length
  snapshot = { ...snapshot, delta: null, resyncVersion: 1 }
  shown.rerender(search)
  await waitFor(() =>
    expect(getServiceHistory).toHaveBeenCalledTimes(calls + 1)
  )
  snapshot = {
    ...snapshot,
    status: "error",
    error: new Error("Live interrupted"),
  }
  shown.rerender(search)
  expect(
    await screen.findByText("Live updates unavailable for release.")
  ).toBeVisible()
  await userEvent.click(
    screen.getByRole("button", { name: "Retry live updates for release" })
  )
  expect(retry).toHaveBeenCalledOnce()
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
  await screen.findByRole("heading", { name: "Build" })
  const selector = screen.getByLabelText("Container grouping")
  expect(
    within(selector).getByRole("option", { name: "Environment" })
  ).toBeVisible()
  expect(
    within(selector).getByRole("option", { name: "Component" })
  ).toBeVisible()
  await userEvent.selectOptions(selector, "metadata:component")
  expect(shown.change).toHaveBeenLastCalledWith({
    ...search,
    groupBy: "metadata",
    groupKey: "component",
  })
  shown.rerender({ ...search, groupBy: "metadata", groupKey: "component" })
  expect(await screen.findByRole("region", { name: "Api" })).toBeVisible()
  expect(getServiceHistory).toHaveBeenCalledTimes(1)
  shown.rerender({ ...search, groupBy: "metadata", groupKey: "type" })
  expect(
    await screen.findByRole("region", { name: "Custom Type" })
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
  await screen.findByRole("heading", { name: "Build" })
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

it("uses capitalized code/config names and metadata labels without changing IDs or grouping values", async () => {
  vi.mocked(getTeams).mockResolvedValue([
    {
      team: "team",
      typeCount: 1,
      presentation: {
        typeOrder: [],
        groupingKeys: [],
        listColumns: [],
        defaultFilters: {},
        labels: { component: "Component" },
        names: {
          team: "Demo",
          "team/release/build": "Five Minute Demo",
          "dataflow-demo": "Dataflow Demo",
        },
      },
    },
  ])
  vi.mocked(getServiceHistory).mockResolvedValue({
    ...response,
    items: [
      {
        ...response.items[0]!,
        workflow: {
          ...response.items[0]!.workflow,
          metadata: { type: "release", values: { component: "dataflow-demo" } },
        },
      },
    ],
  })
  const shown = show()
  expect(
    await screen.findByRole("heading", { name: "Demo Service History" })
  ).toBeVisible()
  expect(
    await screen.findByRole("heading", { name: "Five Minute Demo" })
  ).toBeVisible()
  expect(screen.getByRole("option", { name: "Component" })).toHaveValue(
    "metadata:component"
  )
  shown.rerender({ ...search, groupBy: "metadata", groupKey: "component" })
  expect(
    await screen.findByRole("region", { name: "Dataflow Demo" })
  ).toBeVisible()
  expect(
    screen.getByRole("link", { name: "History for team/release/build" })
  ).toHaveAttribute("href", "/t/team/release/w/build/history")
  expect(getServiceHistory).toHaveBeenCalledTimes(1)
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
  await screen.findByRole("option", { name: "Private Key" })
  vi.mocked(getServiceHistory).mockRejectedValue(
    new ApiError(new Response(null, { status: 403 }), { title: "Revoked" })
  )
  await shown.client.invalidateQueries({
    queryKey: ["catalog", "team", "team", "service-history"],
  })
  expect(await screen.findByRole("alert")).toHaveTextContent("Revoked")
  expect(
    screen.queryByRole("option", { name: "Private Key" })
  ).not.toBeInTheDocument()
  expect(
    screen.queryByRole("heading", { name: "Build" })
  ).not.toBeInTheDocument()
})
