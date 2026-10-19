import { createElement, type ReactNode } from "react"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { renderHook, waitFor } from "@testing-library/react"
import { beforeEach, describe, expect, it, vi } from "vitest"
import { ApiError } from "@/lib/api/client"
import { getTeams, type CatalogWorkflow, type Presentation } from "./api"
import {
  groupWorkflows,
  metadataValue,
  orderTypes,
  presentationLabel,
  presentationName,
  usePresentation,
} from "./usePresentation"

vi.mock("./api", async (original) => ({
  ...(await original<typeof import("./api")>()),
  getTeams: vi.fn(),
}))

const config: Presentation = {
  typeOrder: ["perf", "release"],
  groupingKeys: ["environment", "component"],
  listColumns: ["component", "environment"],
  defaultFilters: { environment: "dev" },
  labels: { environment: "Environment", component: "Component" },
}
const version = { id: "019ed132-2f44-7000-8000-000000000001", semVer: "1.0.0" }
function workflow(
  name: string,
  values: Record<string, string>
): CatalogWorkflow {
  return {
    id: `team/release/${name}`,
    team: "team",
    type: "release",
    name,
    activeVersion: version,
    latestVersion: version,
    metadata: { type: "release", values },
    latestRun: null,
  }
}
function wrapper(client: QueryClient) {
  return ({ children }: { children: ReactNode }) =>
    createElement(QueryClientProvider, { client }, children)
}

describe("scoped team presentation", () => {
  beforeEach(() => vi.resetAllMocks())

  it("selects only the current team's configuration when navigating between teams", async () => {
    vi.mocked(getTeams).mockResolvedValue([
      { team: "team-a", typeCount: 2, presentation: config },
      { team: "team-b", typeCount: 1, presentation: null },
    ])
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    })
    const view = renderHook(({ team }) => usePresentation(team), {
      initialProps: { team: "team-a" },
      wrapper: wrapper(client),
    })
    await waitFor(() => expect(view.result.current.data).toEqual(config))
    view.rerender({ team: "team-b" })
    await waitFor(() => expect(view.result.current.data).toBeNull())
    expect(getTeams).toHaveBeenCalledOnce()
  })

  it("does not invent configuration for an inaccessible team", async () => {
    vi.mocked(getTeams).mockResolvedValue([])
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    })
    const view = renderHook(() => usePresentation("hidden"), {
      wrapper: wrapper(client),
    })
    await waitFor(() =>
      expect(view.result.current.error?.message).toContain("not available")
    )
    expect(view.result.current.data).toBeUndefined()
  })

  it("clears cached presentation after access is revoked", async () => {
    vi.mocked(getTeams).mockResolvedValueOnce([
      { team: "team-a", typeCount: 2, presentation: config },
    ])
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false } },
    })
    const view = renderHook(() => usePresentation("team-a"), {
      wrapper: wrapper(client),
    })
    await waitFor(() => expect(view.result.current.data).toEqual(config))
    vi.mocked(getTeams).mockRejectedValue(
      new ApiError(new Response(null, { status: 403 }), { title: "Revoked" })
    )
    await client.invalidateQueries({ queryKey: ["catalog", "teams"] })
    await waitFor(() => expect(view.result.current.data).toBeUndefined())
    expect(view.result.current.error?.message).toBe("Revoked")
  })

  it("orders configured types first and preserves unlisted type order without mutating the source", () => {
    const types = [
      { name: "release" },
      { name: "extra" },
      { name: "perf" },
      { name: "other" },
    ]
    expect(orderTypes(types, config).map((type) => type.name)).toEqual([
      "perf",
      "release",
      "extra",
      "other",
    ])
    expect(types.map((type) => type.name)).toEqual([
      "release",
      "extra",
      "perf",
      "other",
    ])
    expect(orderTypes(types, null)).toEqual(types)
  })

  it("groups by exact tuples across all loaded pages with labels and missing values", () => {
    const first = workflow("first", { environment: "dev", component: "api" })
    const other = workflow("other", { environment: "dev", component: "web" })
    const next = workflow("next", { environment: "dev", component: "api" })
    const missing = workflow("missing", {})
    const groups = groupWorkflows([first, other, next, missing], config)
    expect(groups.map((group) => group.label)).toEqual([
      "Environment: dev / Component: api",
      "Environment: dev / Component: web",
      "Environment: Not set / Component: Not set",
    ])
    expect(groups[0]?.items.map((item) => item.name)).toEqual(["first", "next"])
    expect(groupWorkflows([first, next], null)).toEqual([
      {
        id: "[]",
        label: null,
        items: [first, next],
      },
    ])
  })

  it("does not merge tuple values containing separators or actual placeholder text", () => {
    const groups = groupWorkflows(
      [
        workflow("a", { environment: "one / two", component: "three" }),
        workflow("b", { environment: "one", component: "two / three" }),
        workflow("c", {}),
        workflow("d", { environment: "Not set", component: "Not set" }),
      ],
      config
    )
    expect(new Set(groups.map((group) => group.id)).size).toBe(4)
  })

  it("uses explicit capitalized names while preserving raw IDs, grouping values and acronyms", () => {
    const named = {
      ...config,
      names: {
        team: "Demo",
        "team/release/five-minute-demo": "Five Minute Demo",
        "dataflow-demo": "Dataflow Demo",
      },
    }
    expect(presentationName(named, "team")).toBe("Demo")
    expect(
      presentationName(
        named,
        "five-minute-demo",
        "team/release/five-minute-demo"
      )
    ).toBe("Five Minute Demo")
    expect(presentationName(named, "dataflow-demo")).toBe("Dataflow Demo")
    expect(presentationName(null, "API-service")).toBe("API Service")
    expect(presentationName(null, "team_name")).toBe("Team Name")
    expect(presentationName(named, "toString")).toBe("ToString")
    expect(named.names["team/release/five-minute-demo"]).toBe(
      "Five Minute Demo"
    )
  })

  it("never resolves inherited JavaScript property names as metadata or labels", () => {
    expect(metadataValue(workflow("empty", {}), "toString")).toBeUndefined()
    expect(presentationLabel(config, "toString")).toBe("toString")
    expect(
      metadataValue(workflow("value", { toString: "literal" }), "toString")
    ).toBe("literal")
  })
})
