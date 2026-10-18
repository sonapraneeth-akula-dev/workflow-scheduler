import type { ReactNode } from "react"
import { fireEvent, render, screen, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { beforeEach, describe, expect, it, vi } from "vitest"
import type { VersionPlan } from "@/features/taskplan/api"
import { executionStatuses, type RunSnapshot } from "@/lib/live/runApi"
import { layoutGraph } from "./layout"
import { RunGraph } from "./RunGraph"

vi.mock("./layout", async (original) => {
  const module = await original<typeof import("./layout")>()
  return { ...module, layoutGraph: vi.fn(module.layoutGraph) }
})
vi.mock("@tanstack/react-router", () => ({
  Link: ({
    to,
    params,
    children,
  }: {
    to: string
    params: Record<string, string>
    children: ReactNode
  }) => (
    <a
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
const versionId = "019ed132-2f44-7000-8000-000000000001"
const workflowId = "team/type/workflow"
const plan: VersionPlan = {
  versionId,
  workflowId,
  version: "1.0.0",
  runTimeout: "01:00:00",
  stages: [],
  placement: null,
  nodes: [
    {
      key: "start",
      dependencies: [],
      tasks: [],
      mode: "Series",
      failure: "Hard",
      maxParallel: null,
      timeout: null,
      placement: null,
    },
    {
      key: "finish",
      dependencies: [{ node: "start", when: "Always" }],
      mode: "Series",
      failure: "Hard",
      maxParallel: null,
      timeout: null,
      placement: null,
      tasks: [
        {
          key: "sdk",
          action: { kind: "SdkFunction", declaration: "Example.Run" },
          placement: null,
          failure: "Hard",
          secretReference: null,
          requestTimeout: null,
          policy: {
            attemptTimeout: "00:01:00",
            autoRetry: true,
            retry: {
              retries: 0,
              on: [],
              backoff: {
                kind: "Fixed",
                delay: "00:00:01",
                maxDelay: "00:01:00",
              },
            },
          },
        },
      ],
    },
  ],
}
const run: RunSnapshot = {
  runId: "019ed132-2f44-7000-8000-000000000002",
  versionId,
  workflowId,
  seriesId: null,
  status: 1,
  lastSeq: 1,
  nodes: { start: 5, finish: 3 },
  tasks: { "finish/sdk": 3 },
  endedAt: null,
  parentRunId: null,
  reusedNodes: {},
  abort: null,
}

describe("live graph rendering", () => {
  beforeEach(() => vi.clearAllMocks())

  it("uses compact open chevrons aligned to the endpoint with matching semantic strokes", () => {
    const { container } = render(<RunGraph plan={plan} run={run} />)
    const marker = container.querySelector("marker")!
    expect(marker).toHaveAttribute("markerUnits", "userSpaceOnUse")
    expect(marker).toHaveAttribute("viewBox", "0 0 8 10")
    expect(marker).toHaveAttribute("refX", "6")
    expect(marker).toHaveAttribute("markerWidth", "8")
    expect(marker).toHaveAttribute("orient", "auto")
    const head = marker.querySelector("path")!
    expect(head).toHaveAttribute("d", "M1 1 L6 5 L1 9")
    expect(head).toHaveAttribute("fill", "none")
    expect(head).toHaveAttribute("stroke-width", "2")
    expect(head).toHaveAttribute("stroke-linejoin", "round")
    const edge = container.querySelector("[data-graph-edge]")!
    expect(edge).toHaveAttribute("stroke-width", "2")
    expect(edge).toHaveAttribute("marker-end", `url(#${marker.id})`)
    expect(edge).toHaveAttribute("stroke-dasharray", "6 3")
  })

  it("preserves layout, edges and node DOM identity across live frame batches", () => {
    const { rerender, container } = render(<RunGraph plan={plan} run={run} />)
    const node = screen.getByRole("button", { name: "finish: Running" })
    const position = node.getAttribute("style")
    const edge = container.querySelector("[data-graph-edge]")
    const path = edge?.getAttribute("d")
    for (let sequence = 2; sequence <= 101; sequence++)
      rerender(
        <RunGraph
          plan={plan}
          run={{
            ...run,
            lastSeq: sequence,
            nodes: { ...run.nodes, finish: sequence % 2 ? 3 : 4 },
          }}
        />
      )
    expect(layoutGraph).toHaveBeenCalledTimes(1)
    expect(screen.getByRole("button", { name: "finish: Running" })).toBe(node)
    expect(node).toHaveAttribute("style", position)
    expect(container.querySelector("[data-graph-edge]")).toBe(edge)
    expect(edge).toHaveAttribute("d", path)
    expect(
      screen.getByRole("region", { name: "Run dependency graph" })
    ).toHaveAttribute("data-run-seq", "101")
  })

  it("lays out a changed immutable topology once, without retaining stale nodes", () => {
    const { rerender } = render(<RunGraph plan={plan} run={run} />)
    rerender(
      <RunGraph
        plan={{ ...plan, nodes: [plan.nodes[0]!] }}
        run={{ ...run, nodes: { start: 5 }, tasks: {} }}
      />
    )
    expect(layoutGraph).toHaveBeenCalledTimes(2)
    expect(
      screen.queryByRole("button", { name: /finish/ })
    ).not.toBeInTheDocument()
  })

  it("selects a node and links the precise task to its historical pinned plan", () => {
    render(<RunGraph plan={plan} run={run} />)
    fireEvent.click(screen.getByRole("button", { name: "finish: Running" }))
    const details = screen.getByRole("region", { name: "Selected node finish" })
    expect(details).toHaveTextContent("Dependencies: start (Always)")
    expect(
      screen.getByRole("link", { name: "sdk: Task Plan" })
    ).toHaveAttribute(
      "href",
      `/t/team/type/w/workflow/versions/${versionId}/tasks/finish%2Fsdk`
    )
    fireEvent.click(screen.getByRole("button", { name: "start: Succeeded" }))
    expect(
      screen.getByRole("region", { name: "Selected node start" })
    ).toHaveTextContent("No declared tasks")
  })

  it("links reused node inspection to its exact source rather than inventing attempts on the rerun", () => {
    render(
      <RunGraph
        plan={plan}
        run={{
          ...run,
          parentRunId: versionId,
          reusedNodes: { start: versionId },
          nodes: { ...run.nodes, start: 11 },
        }}
      />
    )
    fireEvent.click(screen.getByRole("button", { name: "start: Reused" }))
    expect(
      screen.getByRole("region", { name: "Selected node start" })
    ).toHaveTextContent("Reused")
    expect(
      screen.getByRole("link", {
        name: "Original outputs and attempts for start",
      })
    ).toHaveAttribute("href", `/runs/${versionId}`)
    fireEvent.click(screen.getByRole("button", { name: "finish: Running" }))
    expect(
      screen.queryByRole("link", { name: /Original outputs and attempts/ })
    ).not.toBeInTheDocument()
  })

  it("uses one tab stop, geometric arrows and Home/End without trapping Tab", async () => {
    const user = userEvent.setup()
    const nodes: VersionPlan["nodes"] = [
      plan.nodes[0]!,
      { ...plan.nodes[0]!, key: "sibling" },
      plan.nodes[1]!,
      {
        ...plan.nodes[1]!,
        key: "peer",
        dependencies: [{ node: "sibling", when: "OnSuccess" }],
      },
    ]
    const { rerender } = render(
      <RunGraph
        plan={{ ...plan, nodes }}
        run={{ ...run, nodes: { start: 5, sibling: 3, finish: 3, peer: 3 } }}
      />
    )
    const first = screen.getByRole("button", { name: "start: Succeeded" })
    const last = screen.getByRole("button", { name: "peer: Running" })
    const graph = screen.getByRole("region", { name: "Run dependency graph" })
    expect(graph.querySelectorAll('button[tabindex="0"]')).toHaveLength(1)
    await user.tab()
    expect(first).toHaveFocus()
    await user.keyboard("{ArrowLeft}{ArrowUp}")
    expect(first).toHaveFocus()
    await user.keyboard("{ArrowDown}")
    expect(
      screen.getByRole("button", { name: "sibling: Running" })
    ).toHaveFocus()
    await user.keyboard("{ArrowRight}")
    expect(last).toHaveFocus()
    await user.keyboard("{ArrowUp}")
    expect(
      screen.getByRole("button", { name: "finish: Running" })
    ).toHaveFocus()
    await user.keyboard("{ArrowLeft}")
    expect(first).toHaveFocus()
    await user.keyboard("{End}")
    expect(last).toHaveFocus()
    await user.keyboard("{End}{ArrowRight}{ArrowDown}")
    expect(last).toHaveFocus()
    await user.keyboard(" ")
    expect(last).toHaveAttribute("aria-pressed", "true")
    expect(
      screen.getByRole("region", { name: "Selected node peer" })
    ).toBeVisible()
    rerender(
      <RunGraph
        plan={{ ...plan, nodes }}
        run={{
          ...run,
          lastSeq: 2,
          nodes: { start: 5, sibling: 3, finish: 3, peer: 4 },
        }}
      />
    )
    expect(last).toHaveFocus()
    expect(last).toHaveAccessibleName("peer: Retrying")
    expect(last).toHaveAttribute("tabindex", "0")
    await user.keyboard("{Home}{Enter}")
    expect(first).toHaveFocus()
    expect(first).toHaveAttribute("aria-pressed", "true")
    await user.tab()
    expect(screen.getByText("Node list")).toHaveFocus()
    expect(layoutGraph).toHaveBeenCalledTimes(1)
  })

  it.each(executionStatuses)(
    "renders a decorative icon and readable %s text",
    (status) => {
      render(
        <RunGraph
          plan={plan}
          run={{
            ...run,
            nodes: { ...run.nodes, start: executionStatuses.indexOf(status) },
          }}
        />
      )
      const node = screen.getByRole("button", { name: `start: ${status}` })
      expect(node).toHaveTextContent(status)
      expect(node.querySelector("svg")).toHaveAttribute("aria-hidden", "true")
      expect(node.querySelector("svg")).toHaveAttribute("focusable", "false")
    }
  )

  it("provides a keyboard-operable node table with live statuses and dependency conditions", async () => {
    const user = userEvent.setup()
    const { rerender } = render(<RunGraph plan={plan} run={run} />)
    expect(
      screen.queryByRole("table", { name: "Run nodes" })
    ).not.toBeInTheDocument()
    await user.click(screen.getByText("Node list"))
    const table = await screen.findByRole("table", { name: "Run nodes" })
    expect(
      within(table)
        .getAllByRole("columnheader")
        .map((cell) => cell.textContent)
    ).toEqual(["Node", "Status", "Dependencies"])
    expect(within(table).getAllByRole("row")).toHaveLength(
      plan.nodes.length + 1
    )
    expect(table).toHaveTextContent("Succeeded")
    expect(table).toHaveTextContent("start (Always)")
    expect(table).toHaveTextContent("None")
    await user.click(
      screen.getByRole("button", { name: "Inspect node finish" })
    )
    expect(
      screen.getByRole("region", { name: "Selected node finish" })
    ).toBeVisible()
    expect(
      screen.getByRole("link", { name: "sdk: Task Plan" })
    ).toHaveAttribute(
      "href",
      `/t/team/type/w/workflow/versions/${versionId}/tasks/finish%2Fsdk`
    )
    rerender(
      <RunGraph
        plan={plan}
        run={{
          ...run,
          nodes: {
            ...run.nodes,
            finish: executionStatuses.indexOf("Succeeded"),
          },
        }}
      />
    )
    const selected = within(table)
      .getByRole("button", { name: "Inspect node finish" })
      .closest("tr")
    expect(selected).toHaveTextContent("Succeeded")
    expect(selected).toHaveAttribute("data-selected", "true")
    await user.click(screen.getByText("Node list"))
    expect(
      screen.queryByRole("table", { name: "Run nodes" })
    ).not.toBeInTheDocument()
  })

  it.each([
    { ...run, workflowId: "other/type/workflow" },
    { ...run, versionId: run.runId },
    { ...run, nodes: { start: 5 } },
    { ...run, nodes: { start: 5, finish: 3, unknown: 0 } },
    { ...run, nodes: { start: 5, finish: 99 } },
  ])(
    "rejects a mismatched authoritative graph instead of guessing state",
    (snapshot) => {
      expect(() => render(<RunGraph plan={plan} run={snapshot} />)).toThrow(
        "do not match"
      )
    }
  )

  it("renders an explicitly empty workflow without fabricated graph nodes", () => {
    render(
      <RunGraph
        plan={{ ...plan, nodes: [] }}
        run={{ ...run, nodes: {}, tasks: {} }}
      />
    )
    expect(screen.getByText("No declared nodes.")).toBeInTheDocument()
    expect(screen.queryByRole("button")).not.toBeInTheDocument()
  })
})
