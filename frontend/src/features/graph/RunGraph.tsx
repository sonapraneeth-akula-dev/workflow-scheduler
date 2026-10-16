import {
  memo,
  useId,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent,
} from "react"
import { useQuery } from "@tanstack/react-query"
import { Link } from "@tanstack/react-router"
import { ErrorNotice } from "@/components/ErrorNotice"
import { StatusBadge } from "@/components/StatusBadge"
import { Button } from "@/components/ui/button"
import { getVersionPlan, type VersionPlan } from "@/features/taskplan/api"
import { executionStatuses, type RunSnapshot } from "@/lib/live/runApi"
import { GraphNode } from "./GraphNode"
import { layoutGraph, type GraphLayout } from "./layout"

const GraphEdges = memo(function GraphEdges({
  layout,
}: {
  layout: GraphLayout
}) {
  const marker = useId()
  return (
    <svg
      width={layout.width}
      height={layout.height}
      aria-hidden="true"
      className="pointer-events-none absolute top-0 left-0 text-muted-foreground will-change-transform"
    >
      <defs>
        <marker
          id={marker}
          markerWidth="8"
          markerHeight="8"
          refX="7"
          refY="4"
          orient="auto"
        >
          <path d="M0 0 L8 4 L0 8 Z" fill="currentColor" />
        </marker>
      </defs>
      {layout.edges.map((edge) => (
        <path
          key={`${edge.from}/${edge.to}`}
          data-graph-edge={`${edge.from}/${edge.to}`}
          d={edge.path}
          fill="none"
          stroke="currentColor"
          strokeWidth="1.5"
          strokeDasharray={
            edge.when === "Always"
              ? "6 3"
              : edge.when === "OnFailure"
                ? "2 3"
                : undefined
          }
          markerEnd={`url(#${marker})`}
        >
          <title>
            {edge.from} to {edge.to} ({edge.when})
          </title>
        </path>
      ))}
    </svg>
  )
})

export function RunGraph({
  plan,
  run,
}: {
  plan: VersionPlan
  run: RunSnapshot
}) {
  const layout = useMemo(() => layoutGraph(plan.nodes), [plan.nodes])
  const [selected, select] = useState<string | null>(null)
  const [focused, focus] = useState<string | null>(null)
  const [listOpen, setListOpen] = useState(false)
  const graph = useRef<HTMLDivElement>(null)
  const instructions = useId()
  const active = layout.nodes.some((node) => node.key === focused)
    ? focused
    : layout.nodes[0]?.key
  if (
    plan.versionId !== run.versionId ||
    plan.workflowId !== run.workflowId ||
    Object.keys(run.nodes).length !== plan.nodes.length ||
    plan.nodes.some(
      (node) =>
        !Object.hasOwn(run.nodes, node.key) ||
        executionStatuses[run.nodes[node.key]!] === undefined
    )
  )
    throw new Error(
      "The pinned manifest and authoritative run graph do not match."
    )
  const node = plan.nodes.find((node) => node.key === selected)
  const [team = "", type = "", wf = ""] = plan.workflowId.split("/")
  function navigate(event: KeyboardEvent<HTMLDivElement>) {
    if (!(event.target instanceof Element)) return
    const key =
      event.target.closest<HTMLElement>("[data-graph-node]")?.dataset.graphNode
    const current = layout.nodes.find((node) => node.key === key)
    if (!current || event.altKey || event.ctrlKey || event.metaKey) return
    let target
    if (event.key === "Home") target = layout.nodes[0]
    else if (event.key === "End") target = layout.nodes.at(-1)
    else if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
      const direction = event.key === "ArrowLeft" ? -1 : 1
      target = layout.nodes
        .filter((node) => (node.rank - current.rank) * direction > 0)
        .sort(
          (a, b) =>
            Math.abs(a.rank - current.rank) - Math.abs(b.rank - current.rank) ||
            Math.abs(a.style.top - current.style.top) -
              Math.abs(b.style.top - current.style.top)
        )[0]
    } else if (event.key === "ArrowUp" || event.key === "ArrowDown") {
      const direction = event.key === "ArrowUp" ? -1 : 1
      target = layout.nodes
        .filter(
          (node) =>
            node.rank === current.rank &&
            (node.style.top - current.style.top) * direction > 0
        )
        .sort(
          (a, b) =>
            Math.abs(a.style.top - current.style.top) -
            Math.abs(b.style.top - current.style.top)
        )[0]
    } else return
    event.preventDefault()
    if (target) {
      const index = layout.nodes.indexOf(target)
      const buttons =
        graph.current?.querySelectorAll<HTMLButtonElement>("[data-graph-node]")
      buttons?.[index]?.focus()
    }
  }
  return (
    <section aria-label="Live run graph" className="min-w-0">
      <h2 className="mb-2 text-xl font-semibold">Dependency graph</h2>
      <p id={instructions} className="mb-3 text-sm text-muted-foreground">
        Use arrow keys to move between nodes, Home/End for the first/last node,
        and Enter or Space to inspect tasks and the historical Task Plan. Tab
        leaves the graph. Scroll to explore, or expand the node list.
      </p>
      {layout.nodes.length === 0 ? (
        <p>No declared nodes.</p>
      ) : (
        <div
          role="region"
          aria-label="Run dependency graph"
          aria-describedby={instructions}
          ref={graph}
          onKeyDown={navigate}
          data-run-seq={run.lastSeq}
          data-version-id={plan.versionId}
          className="max-h-[70vh] overflow-auto rounded-md border bg-muted/20"
        >
          <div
            className="relative"
            style={{ width: layout.width, height: layout.height }}
          >
            <GraphEdges layout={layout} />
            {layout.nodes.map((position) => (
              <GraphNode
                key={position.key}
                position={position}
                status={run.nodes[position.key]!}
                selected={selected === position.key}
                tabIndex={active === position.key ? 0 : -1}
                onFocus={focus}
                onSelect={select}
              />
            ))}
          </div>
        </div>
      )}
      {layout.nodes.length ? (
        <details
          className="mt-3"
          onToggle={(event) => setListOpen(event.currentTarget.open)}
        >
          <summary className="cursor-pointer">Node list</summary>
          {listOpen ? (
            <ol aria-label="Run nodes" className="mt-2 space-y-3 break-words">
              {plan.nodes.map((item) => (
                <li key={item.key}>
                  <button
                    type="button"
                    aria-label={`Inspect node ${item.key}`}
                    aria-pressed={selected === item.key}
                    className="underline underline-offset-4"
                    onClick={() => select(item.key)}
                  >
                    {item.key}
                  </button>{" "}
                  <StatusBadge
                    status={executionStatuses[run.nodes[item.key]!]!}
                  />
                  <p>
                    Dependencies:{" "}
                    {item.dependencies
                      .map(
                        (dependency) =>
                          `${dependency.node} (${dependency.when})`
                      )
                      .join(", ") || "None"}
                    .
                  </p>
                </li>
              ))}
            </ol>
          ) : null}
        </details>
      ) : null}
      {node ? (
        <section
          aria-label={`Selected node ${node.key}`}
          className="mt-4 min-w-0 rounded-md border p-4 break-words"
        >
          <h3 className="font-semibold">{node.key}</h3>
          <p>
            Dependencies:{" "}
            {node.dependencies
              .map((dependency) => `${dependency.node} (${dependency.when})`)
              .join(", ") || "None"}
            .
          </p>
          {node.tasks.length === 0 ? (
            <p>No declared tasks.</p>
          ) : (
            <ul className="mt-3 flex flex-col gap-2">
              {node.tasks.map((task) => (
                <li key={task.key}>
                  <Link
                    to="/t/$team/$type/w/$wf/versions/$v/tasks/$taskKey"
                    params={{
                      team,
                      type,
                      wf,
                      v: plan.versionId,
                      taskKey: `${node.key}/${task.key}`,
                    }}
                  >
                    {task.key}: Task Plan
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </section>
      ) : null}
    </section>
  )
}

export function LiveRunGraph({ run }: { run: RunSnapshot }) {
  const query = useQuery({
    queryKey: ["version-plan", run.workflowId, run.versionId],
    queryFn: ({ signal }) =>
      getVersionPlan(run.workflowId, run.versionId, signal),
  })
  if (query.error)
    return (
      <div>
        <ErrorNotice error={query.error} />
        <Button variant="outline" onClick={() => void query.refetch()}>
          Retry graph layout
        </Button>
      </div>
    )
  if (!query.data) return <p role="status">Loading pinned graph layout...</p>
  return <RunGraph key={run.runId} plan={query.data} run={run} />
}
