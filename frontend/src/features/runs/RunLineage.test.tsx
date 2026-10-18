import type { ReactNode } from "react"
import { render, screen, within } from "@testing-library/react"
import { describe, expect, it, vi } from "vitest"
import type { RunSnapshot } from "@/lib/live/runApi"
import { RunLineage } from "./RunLineage"

vi.mock("@tanstack/react-router", () => ({
  Link: ({
    params,
    children,
    ...props
  }: {
    params: { id: string }
    children: ReactNode
    "aria-label"?: string
  }) => (
    <a
      href={`/runs/${encodeURIComponent(params.id)}`}
      aria-label={props["aria-label"]}
    >
      {children}
    </a>
  ),
}))
const parent = "019ed132-2f44-7000-8000-000000000001"
const run: RunSnapshot = {
  runId: "019ed132-2f44-7000-8000-000000000002",
  workflowId: "team/type/workflow",
  versionId: parent,
  seriesId: null,
  status: 0,
  lastSeq: 0,
  nodes: { build: 11, deploy: 0 },
  tasks: { "build/compile": 11, "deploy/task": 0 },
  abort: null,
  endedAt: null,
  parentRunId: parent,
  reusedNodes: { build: parent },
}
describe("immutable run lineage", () => {
  it("renders only actual reused nodes and original source links without fabricating attempts or outputs", () => {
    render(<RunLineage run={run} />)
    const lineage = screen.getByRole("region", { name: "Run lineage" })
    expect(within(lineage).getByRole("link", { name: parent })).toHaveAttribute(
      "href",
      `/runs/${parent}`
    )
    expect(within(lineage).getByText("Reused", { exact: true })).toBeVisible()
    expect(
      within(lineage).getByRole("link", {
        name: `build: original outputs in run ${parent}`,
      })
    ).toHaveAttribute("href", `/runs/${parent}`)
    expect(lineage).toHaveTextContent("did not execute new attempts")
    expect(lineage).toHaveTextContent("access is checked there")
    expect(within(lineage).getAllByRole("listitem")).toHaveLength(1)
  })
  it("keeps parent lineage visible when all nodes execute again", () => {
    render(
      <RunLineage
        run={{ ...run, nodes: { build: 0, deploy: 0 }, reusedNodes: {} }}
      />
    )
    expect(screen.getByRole("link", { name: parent })).toBeVisible()
    expect(
      screen.getByText("No nodes reused from the source run.")
    ).toBeVisible()
  })
  it("has no lineage section for ordinary runs", () => {
    render(<RunLineage run={{ ...run, parentRunId: null, reusedNodes: {} }} />)
    expect(screen.queryByRole("region")).not.toBeInTheDocument()
  })
  it("renders node keys as inert text", () => {
    const name = "<img src=x onerror=alert(1)>"
    const { container } = render(
      <RunLineage
        run={{ ...run, nodes: { [name]: 11 }, reusedNodes: { [name]: parent } }}
      />
    )
    expect(screen.getByRole("listitem")).toHaveTextContent(name)
    expect(container.querySelector("img")).toBeNull()
  })
})
