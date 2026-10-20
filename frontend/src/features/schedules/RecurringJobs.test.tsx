import type { ReactNode } from "react"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen, waitFor, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { beforeEach, expect, it, vi } from "vitest"
import { getTeams, getTypes } from "@/features/catalog/api"
import { getPermissions, ApiError } from "@/lib/api/client"
import { runNow } from "@/features/series/api"
import { RecurringJobs } from "./RecurringJobs"
import {
  getRecurringJobs,
  type RecurringJob,
  type RecurringJobsSearch,
} from "./recurring-jobs-api"

vi.mock("./recurring-jobs-api", async (original) => ({
  ...(await original<typeof import("./recurring-jobs-api")>()),
  getRecurringJobs: vi.fn(),
}))
vi.mock("@/features/catalog/api", async (original) => ({
  ...(await original<typeof import("@/features/catalog/api")>()),
  getTeams: vi.fn(),
  getTypes: vi.fn(),
}))
vi.mock("@/lib/api/client", async (original) => ({
  ...(await original<typeof import("@/lib/api/client")>()),
  getPermissions: vi.fn(),
}))
vi.mock("@/features/series/api", async (original) => ({
  ...(await original<typeof import("@/features/series/api")>()),
  runNow: vi.fn(),
}))
vi.mock("@tanstack/react-router", () => ({
  Link: ({
    to,
    params,
    children,
    ...props
  }: {
    to: string
    params: Record<string, string>
    children: ReactNode
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
const job: RecurringJob = {
  id: "019ed132-2f44-7000-8000-000000000001",
  seriesId: "019ed132-2f44-7000-8000-000000000002",
  workflowId: "alpha/regression/linear-regression",
  name: "Training and validation",
  origin: "Code",
  sourcePath: "workflow.schedules.json",
  gitCommit: "a".repeat(40),
  timezone: "Etc/UTC",
  cron: null,
  interval: "00:05:00",
  versionPin: "1.3.0",
  overlap: "Skip",
  catchUp: "Latest",
  variables: { epochs: 200 },
  paused: false,
  rowVersion: 1,
  nextFireAt: "2026-10-20T12:00:00.123456Z",
}
const paused: RecurringJob = {
  ...job,
  id: "019ed132-2f44-7000-8000-000000000003",
  seriesId: "019ed132-2f44-7000-8000-000000000004",
  workflowId: "alpha/regression/logistic-regression",
  name: "Paused logistic experiment",
  origin: "User",
  sourcePath: null,
  gitCommit: null,
  cron: "0 9 * * 1-5",
  interval: null,
  versionPin: null,
  paused: true,
  nextFireAt: null,
}
const fullTest: RecurringJob = {
  ...job,
  id: "019ed132-2f44-7000-8000-000000000009",
  seriesId: "019ed132-2f44-7000-8000-000000000010",
  name: "Full test",
}
const otherType: RecurringJob = {
  ...job,
  id: "019ed132-2f44-7000-8000-000000000005",
  seriesId: "019ed132-2f44-7000-8000-000000000006",
  workflowId: "alpha/validation/data-check",
  name: "Validate data",
}
const otherTeam: RecurringJob = {
  ...job,
  id: "019ed132-2f44-7000-8000-000000000007",
  seriesId: "019ed132-2f44-7000-8000-000000000008",
  workflowId: "bravo/regression/logistic",
  name: "Bravo training",
}
const response = {
  items: [job, paused, otherType, otherTeam],
  nextCursor: null,
}
function show(search: RecurringJobsSearch = {}) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  const change = vi.fn()
  const tree = (next = search) => (
    <QueryClientProvider client={client}>
      <RecurringJobs search={next} onChange={change} />
    </QueryClientProvider>
  )
  const rendered = render(tree())
  return {
    client,
    change,
    rerender: (next: RecurringJobsSearch) => rendered.rerender(tree(next)),
  }
}
beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(getRecurringJobs).mockResolvedValue(response)
  vi.mocked(getTeams).mockResolvedValue([
    {
      team: "alpha",
      typeCount: 2,
      presentation: {
        typeOrder: [],
        groupingKeys: [],
        listColumns: [],
        defaultFilters: {},
        labels: {},
        names: {
          alpha: "Alpha Science",
          "alpha/regression": "Regression",
          "alpha/regression/linear-regression": "Linear Regression - Housing",
        },
      },
    },
    { team: "bravo", typeCount: 1, presentation: null },
  ])
  vi.mocked(getTypes).mockResolvedValue(
    ["regression", "validation"].map((name) => ({
      team: "alpha",
      name,
      description: name,
      metadataSchema: { keys: [], allowFreeForm: true },
      latestStatusCounts: {},
    }))
  )
  vi.mocked(getPermissions).mockImplementation(async (resource) => ({
    ...resource,
    viewer: true,
    author: false,
    operator: true,
    admin: false,
  }))
  vi.mocked(runNow).mockResolvedValue({
    runId: "019ed132-2f44-7000-8000-000000000099",
  })
})

it("groups table rows by authorized team, workflow type and exact workflow with schedule details and actions", async () => {
  vi.mocked(getRecurringJobs).mockResolvedValue({
    ...response,
    items: [...response.items, fullTest],
  })
  show()
  const alpha = await screen.findByRole("region", {
    name: "Alpha Science recurring jobs",
  })
  const regression = within(alpha).getByRole("region", {
    name: "Alpha Science / Regression",
  })
  const table = within(regression).getByRole("table", {
    name: "Alpha Science / Regression recurring jobs grouped by workflow",
  })
  expect(within(table).getAllByRole("columnheader")).toHaveLength(8)
  expect(table.querySelectorAll("tbody[data-workflow-id]")).toHaveLength(2)
  const linear = within(table).getByRole("rowgroup", {
    name: "Linear Regression - Housing",
  })
  expect(linear).toHaveAttribute("data-workflow-id", job.workflowId)
  expect(within(linear).getAllByRole("row")).toHaveLength(3)
  expect(
    within(linear).getByRole("rowheader", { name: fullTest.name })
  ).toBeVisible()
  expect(linear.querySelector('th[scope="rowgroup"]')).toHaveAttribute(
    "colspan",
    "8"
  )
  expect(
    within(alpha).getByRole("region", { name: "Alpha Science / Validation" })
  ).toBeVisible()
  expect(
    screen.getByRole("region", { name: "Bravo recurring jobs" })
  ).toBeVisible()
  const row = within(linear)
    .getByRole("rowheader", { name: job.name })
    .closest("tr")!
  expect(
    within(linear).getByRole("heading", {
      name: "Linear Regression - Housing",
      level: 4,
    })
  ).toBeVisible()
  expect(within(row).getByText("Code schedule")).toBeVisible()
  expect(within(row).getByText("Every 300 seconds")).toBeVisible()
  expect(within(row).getByText("Etc/UTC")).toBeVisible()
  expect(within(row).getByText("Active", { exact: true })).toBeVisible()
  expect(within(row).getByText("1.3.0")).toBeVisible()
  expect(row.querySelector("time")).toHaveAttribute("datetime", job.nextFireAt)
  expect(
    within(row).getByRole("link", { name: "Schedule details" })
  ).toHaveAttribute("href", `/schedules/${job.id}`)
  expect(
    within(row).getByRole("link", { name: "Series history" })
  ).toHaveAttribute("href", `/series/${job.seriesId}`)
  const pausedRow = screen
    .getByRole("rowheader", { name: paused.name })
    .closest("tr")!
  expect(pausedRow.parentElement).not.toBe(linear)
  expect(pausedRow.parentElement).toHaveAttribute(
    "data-workflow-id",
    paused.workflowId
  )
  expect(within(pausedRow).getByText("UI schedule")).toBeVisible()
  expect(within(pausedRow).getByText("Cron: 0 9 * * 1-5")).toBeVisible()
  expect(
    within(pausedRow).getByText("Paused; no automatic fires")
  ).toBeVisible()
  expect(within(pausedRow).getByText("Active version")).toBeVisible()
  expect(screen.getByText("Showing 5 loaded recurring jobs.")).toBeVisible()
  expect(getTypes).not.toHaveBeenCalled()
  expect(screen.getByRole("combobox", { name: "Workflow type" })).toBeDisabled()
})

it("uses the existing scoped Run now action for the selected paused schedule without resume/edit writes", async () => {
  show()
  const row = (
    await screen.findByRole("rowheader", { name: paused.name })
  ).closest("tr")!
  const button = await within(row).findByRole("button", { name: "Run now" })
  button.focus()
  await userEvent.keyboard("{Enter}")
  expect(await within(row).findByRole("status")).toHaveTextContent(
    "Run accepted in this series"
  )
  expect(within(row).getByRole("status")).toHaveTextContent(
    "next fire and saved variables are unchanged"
  )
  expect(runNow).toHaveBeenCalledExactlyOnceWith(paused.id, expect.any(String))
  expect(getPermissions).toHaveBeenCalledWith(
    { scope: "series", id: paused.seriesId },
    expect.any(AbortSignal)
  )
  expect(
    within(row).getByRole("link", {
      name: "019ed132-2f44-7000-8000-000000000099",
    })
  ).toHaveAttribute("href", "/runs/019ed132-2f44-7000-8000-000000000099")
  expect(
    screen.queryByRole("button", { name: /pause|resume|delete/i })
  ).not.toBeInTheDocument()
})

it("keeps jobs visible to a Viewer but hides Run now without Operator permission", async () => {
  vi.mocked(getPermissions).mockImplementation(async (resource) => ({
    ...resource,
    viewer: true,
    author: true,
    operator: false,
    admin: false,
  }))
  const shown = show()
  await screen.findByRole("rowheader", { name: job.name })
  await waitFor(() => expect(shown.client.isFetching()).toBe(0))
  expect(
    screen.queryByRole("button", { name: "Run now" })
  ).not.toBeInTheDocument()
  expect(runNow).not.toHaveBeenCalled()
})

it("merges additional pages into existing workflow row groups without dropping earlier jobs", async () => {
  vi.mocked(getRecurringJobs).mockImplementation(async (_search, cursor) =>
    cursor
      ? { items: [otherTeam, fullTest], nextCursor: null }
      : { items: [job], nextCursor: "next" }
  )
  show()
  await screen.findByRole("rowheader", { name: job.name })
  await userEvent.click(
    screen.getByRole("button", { name: "Load more recurring jobs" })
  )
  expect(
    await screen.findByRole("rowheader", { name: otherTeam.name })
  ).toBeVisible()
  const linear = screen.getAllByRole("rowgroup", {
    name: "Linear Regression - Housing",
  })
  expect(linear).toHaveLength(1)
  expect(
    within(linear[0]!).getByRole("rowheader", { name: job.name })
  ).toBeVisible()
  expect(
    within(linear[0]!).getByRole("rowheader", { name: fullTest.name })
  ).toBeVisible()
  expect(getRecurringJobs).toHaveBeenLastCalledWith(
    {},
    { cursor: "next", afterId: job.id },
    expect.any(AbortSignal)
  )
  expect(
    screen.queryByRole("button", { name: "Load more recurring jobs" })
  ).not.toBeInTheDocument()
})

it("changes exact URL filters, clears the old type on team change and sends only the selected scope", async () => {
  vi.mocked(getRecurringJobs).mockImplementation(async (search) => ({
    items: response.items.filter((item) => {
      const [team, type] = item.workflowId.split("/")
      return (
        (!search.team || team === search.team) &&
        (!search.type || type === search.type)
      )
    }),
    nextCursor: null,
  }))
  const shown = show()
  await screen.findByRole("rowheader", { name: otherTeam.name })
  await userEvent.selectOptions(
    screen.getByRole("combobox", { name: "Team" }),
    "alpha"
  )
  expect(shown.change).toHaveBeenLastCalledWith({
    team: "alpha",
    type: undefined,
  })
  shown.rerender({ team: "alpha" })
  await waitFor(() =>
    expect(
      screen.getByRole("combobox", { name: "Workflow type" })
    ).toBeEnabled()
  )
  await userEvent.selectOptions(
    screen.getByRole("combobox", { name: "Workflow type" }),
    "regression"
  )
  expect(shown.change).toHaveBeenLastCalledWith({
    team: "alpha",
    type: "regression",
  })
  shown.rerender({ team: "alpha", type: "regression" })
  await waitFor(() =>
    expect(
      screen.queryByRole("rowheader", { name: otherType.name })
    ).not.toBeInTheDocument()
  )
  expect(
    screen.queryByRole("rowheader", { name: otherTeam.name })
  ).not.toBeInTheDocument()
  expect(getRecurringJobs).toHaveBeenLastCalledWith(
    { team: "alpha", type: "regression" },
    null,
    expect.any(AbortSignal)
  )
  await userEvent.selectOptions(
    screen.getByRole("combobox", { name: "Team" }),
    ""
  )
  expect(shown.change).toHaveBeenLastCalledWith({
    team: undefined,
    type: undefined,
  })
})

it("shows loading and empty states without inventing recurring jobs", async () => {
  vi.mocked(getRecurringJobs).mockReturnValue(new Promise(() => {}))
  const shown = show()
  expect(screen.getByText("Loading recurring jobs...")).toBeVisible()
  expect(screen.queryByRole("table")).not.toBeInTheDocument()
  vi.mocked(getRecurringJobs).mockResolvedValue({ items: [], nextCursor: null })
  shown.rerender({ team: "bravo" })
  expect(
    await screen.findByText(
      "No recurring jobs match the selected team and type."
    )
  ).toBeVisible()
})

it("hides cached jobs after failed or revoked reads and reloads after an explicit retry", async () => {
  const shown = show()
  await screen.findByRole("rowheader", { name: job.name })
  vi.mocked(getRecurringJobs).mockRejectedValue(
    new ApiError(new Response(null, { status: 403 }), {
      title: "Viewer access revoked",
    })
  )
  await shown.client.invalidateQueries({ queryKey: ["recurring-jobs"] })
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Viewer access revoked"
  )
  expect(
    screen.queryByRole("rowheader", { name: job.name })
  ).not.toBeInTheDocument()
  expect(
    screen.queryByRole("button", { name: "Run now" })
  ).not.toBeInTheDocument()
  vi.mocked(getRecurringJobs).mockResolvedValue(response)
  await userEvent.click(
    screen.getByRole("button", { name: "Refresh recurring jobs" })
  )
  expect(await screen.findByRole("rowheader", { name: job.name })).toBeVisible()
  await waitFor(() =>
    expect(screen.queryByRole("alert")).not.toBeInTheDocument()
  )
})
