import type { ReactNode } from "react"
import { fireEvent, render, screen, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { beforeEach, describe, expect, it, vi } from "vitest"
import type { VersionPlan } from "@/features/taskplan/api"
import { executionStatuses, type RunSnapshot } from "@/lib/live/runApi"
import { layoutGraph } from "./layout"
import { RunGraph } from "./RunGraph"
import { describeNodeRetries, retryableFailures } from "./policies"

vi.mock("./layout", async (original) => {
  const module = await original<typeof import("./layout")>()
  return { ...module, layoutGraph: vi.fn(module.layoutGraph) }
})
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
    "aria-label"?: string
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

  it("keeps the authored condition visible after decision and exposes inherited task debug controls", () => {
    render(
      <RunGraph
        plan={{
          ...plan,
          nodes: plan.nodes.map((node) =>
            node.key === "finish"
              ? { ...node, when: { variableKey: "enabled", expected: true } }
              : node
          ),
        }}
        run={{
          ...run,
          nodes: { ...run.nodes, finish: 10 },
          tasks: { "finish/sdk": 10 },
          conditionOutcomes: { finish: "NotMatched" },
        }}
      />
    )
    fireEvent.click(screen.getByRole("button", { name: /finish/i }))
    const selected = within(
      screen.getByRole("region", { name: "Selected node finish" })
    )
    expect(
      selected.getByText(
        "Condition: variable enabled equals true; false skips without an attempt."
      )
    ).toBeVisible()
    expect(
      selected.getByText("Condition: false; skipped without an attempt.")
    ).toBeVisible()
    expect(
      selected.getByRole("button", { name: "Show debug details for finish" })
    ).toHaveAttribute("aria-expanded", "false")
    expect(
      selected.getByRole("button", {
        name: "Show debug details for finish/sdk",
      })
    ).toBeVisible()
  })

  it("explains node and task skips from durable condition decisions", () => {
    render(
      <RunGraph
        plan={plan}
        run={{
          ...run,
          nodes: { ...run.nodes, finish: 10 },
          tasks: { "finish/sdk": 10 },
          conditionOutcomes: {
            finish: "NotMatched",
            "finish/sdk": "NotMatched",
          },
        }}
      />
    )
    fireEvent.click(screen.getByRole("button", { name: /finish/i }))
    const selected = screen.getByRole("region", {
      name: "Selected node finish",
    })
    expect(
      within(selected).getAllByText(
        "Condition: false; skipped without an attempt."
      )
    ).toHaveLength(2)
  })

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
      screen.getByRole("link", { name: "View task plan for sdk" })
    ).toHaveAttribute(
      "href",
      `/t/team/type/w/workflow/versions/${versionId}/tasks/finish%2Fsdk`
    )
    fireEvent.click(screen.getByRole("button", { name: "start: Succeeded" }))
    expect(
      screen.getByRole("region", { name: "Selected node start" })
    ).toHaveTextContent("No declared tasks")
  })

  it("marks only soft-failure nodes optional, not required nodes containing a soft-failure task", () => {
    const { container } = render(
      <RunGraph
        plan={{
          ...plan,
          nodes: [
            { ...plan.nodes[0]!, failure: "Soft" },
            {
              ...plan.nodes[1]!,
              tasks: [{ ...plan.nodes[1]!.tasks[0]!, failure: "Soft" }],
            },
          ],
        }}
        run={run}
      />
    )
    const optional = screen.getByRole("button", { name: "start: Succeeded" })
    expect(optional).toHaveAccessibleDescription(
      "0 tasks, sequential. Optional node: soft-failure policy; still runs. No retries configured."
    )
    expect(
      container.querySelectorAll(
        '[title="Optional node: soft-failure policy; still runs."]'
      )
    ).toHaveLength(1)
    expect(
      screen.getByRole("button", { name: "finish: Running" })
    ).toHaveAccessibleDescription(
      "1 task, sequential. Required node: hard-failure policy. Failure retries: 0/1 tasks; platform recovery: 1/1 tasks."
    )
  })

  it.each<{
    label: string
    retries: number
    on: VersionPlan["nodes"][number]["tasks"][number]["policy"]["retry"]["on"]
    autoRetry: boolean
    eligible: number
  }>([
    {
      label: "platform recovery only",
      retries: 0,
      on: ["TaskError", "Timeout"],
      autoRetry: true,
      eligible: 0,
    },
    {
      label: "task failures without platform recovery",
      retries: 2,
      on: ["TaskError", "Timeout"],
      autoRetry: false,
      eligible: 2,
    },
    {
      label: "authored agent failures without automatic recovery",
      retries: 1,
      on: ["AgentLost", "PlatformError"],
      autoRetry: false,
      eligible: 2,
    },
    {
      label: "no budget",
      retries: 0,
      on: ["TaskError"],
      autoRetry: false,
      eligible: 0,
    },
    {
      label: "no categories",
      retries: 2,
      on: [],
      autoRetry: false,
      eligible: 0,
    },
    {
      label: "non-retryable categories",
      retries: 2,
      on: ["ValidationError", "Aborted", "ResourceLimit", "Placement"],
      autoRetry: false,
      eligible: 0,
    },
  ])(
    "shows an accurate configured retry indicator for $label",
    ({ retries, on, autoRetry, eligible }) => {
      const original = plan.nodes[1]!.tasks[0]!
      const task = {
        ...original,
        policy: {
          ...original.policy,
          autoRetry,
          retry: { ...original.policy.retry, retries, on },
        },
      }
      const node = { ...plan.nodes[1]!, tasks: [task] }
      const description = describeNodeRetries(node)
      expect(retryableFailures(task)).toHaveLength(eligible)
      expect(description).toBe(
        eligible || autoRetry
          ? `Failure retries: ${eligible ? 1 : 0}/1 tasks; platform recovery: ${autoRetry ? 1 : 0}/1 tasks.`
          : null
      )
      render(
        <RunGraph plan={{ ...plan, nodes: [plan.nodes[0]!, node] }} run={run} />
      )
      const button = screen.getByRole("button", { name: "finish: Running" })
      if (description) {
        expect(
          within(button).getByTitle(description).querySelector("svg")
        ).toBeInTheDocument()
        expect(button).toHaveAccessibleDescription(
          expect.stringContaining(description)
        )
      } else {
        expect(button.querySelectorAll("svg")).toHaveLength(1)
        expect(button).toHaveAccessibleDescription(
          expect.stringContaining("No retries configured.")
        )
      }
    }
  )

  it("explains multiple tasks, parallel execution, live statuses and per-task retry policies", () => {
    const first = plan.nodes[1]!.tasks[0]!
    const second = {
      ...first,
      key: "notify",
      failure: "Soft" as const,
      policy: {
        ...first.policy,
        autoRetry: false,
        retry: {
          ...first.policy.retry,
          retries: 2,
          on: ["TaskError" as const],
        },
      },
    }
    const { rerender } = render(
      <RunGraph
        plan={{
          ...plan,
          nodes: [
            plan.nodes[0]!,
            {
              ...plan.nodes[1]!,
              mode: "Parallel",
              maxParallel: 2,
              tasks: [first, second],
            },
          ],
        }}
        run={{ ...run, tasks: { "finish/sdk": 3, "finish/notify": 6 } }}
      />
    )
    const button = screen.getByRole("button", { name: "finish: Running" })
    expect(button).toHaveTextContent("2 tasks")
    expect(button).toHaveAccessibleDescription(
      expect.stringContaining(
        "Failure retries: 1/2 tasks; platform recovery: 1/2 tasks."
      )
    )
    fireEvent.click(button)
    const details = screen.getByRole("region", { name: "Selected node finish" })
    expect(
      within(details).getByRole("heading", { name: "Node: finish" })
    ).toBeVisible()
    expect(details).toHaveTextContent(
      "2 tasks run in parallel, up to 2 at a time."
    )
    const tasks = within(details).getByRole("list", {
      name: "Tasks in node finish",
    })
    const rows = within(tasks).getAllByRole("listitem")
    expect(rows).toHaveLength(2)
    expect(rows[0]).toHaveTextContent("sdk Running")
    expect(rows[0]).toHaveTextContent(
      "Failure retries: disabled. Platform recovery: enabled for agent/platform failures."
    )
    expect(rows[1]).toHaveTextContent("notify SoftFailed")
    expect(rows[1]).toHaveTextContent("Soft-failure task.")
    expect(rows[1]).toHaveTextContent(
      "Failure retries: up to 2 (TaskError). Platform recovery: disabled."
    )
    expect(
      within(rows[1]!).getByRole("link", { name: "View task plan for notify" })
    ).toHaveAttribute(
      "href",
      `/t/team/type/w/workflow/versions/${versionId}/tasks/finish%2Fnotify`
    )
    rerender(
      <RunGraph
        plan={plan}
        run={{ ...run, lastSeq: 2, tasks: { "finish/sdk": 5 } }}
      />
    )
    expect(details).toHaveTextContent(
      "1 task runs sequentially, in the order below."
    )
    expect(details).toHaveTextContent("sdk Succeeded")
    expect(details).not.toHaveTextContent("notify")
  })

  it("does not invent a status for an unrecorded task", () => {
    render(<RunGraph plan={plan} run={{ ...run, tasks: {} }} />)
    fireEvent.click(screen.getByRole("button", { name: "finish: Running" }))
    const details = screen.getByRole("region", { name: "Selected node finish" })
    expect(details).toHaveTextContent("Status not recorded")
    expect(
      within(details).queryByText("Pending", { exact: true })
    ).not.toBeInTheDocument()
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

  it("provides a keyboard-operable node table and preserves live selection when collapsed", async () => {
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
    ).toEqual(["Node", "Status", "Tasks", "Policies", "Dependencies"])
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
      screen.getByRole("link", { name: "View task plan for sdk" })
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
    expect(
      within(table).getByRole("button", { name: "Inspect node finish" })
    ).toHaveFocus()
    await user.click(screen.getByText("Node list"))
    expect(
      screen.queryByRole("table", { name: "Run nodes" })
    ).not.toBeInTheDocument()
    expect(
      screen.getByRole("region", { name: "Selected node finish" })
    ).toBeVisible()
    await user.click(screen.getByText("Node list"))
    expect(
      within(await screen.findByRole("table", { name: "Run nodes" }))
        .getByRole("button", { name: "Inspect node finish" })
        .closest("tr")
    ).toHaveAttribute("data-selected", "true")
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
