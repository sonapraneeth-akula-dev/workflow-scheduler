import { memo, useId, useMemo, useState } from "react"
import { useQuery } from "@tanstack/react-query"
import { Link } from "@tanstack/react-router"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
import { getVersionPlan, type VersionPlan } from "@/features/taskplan/api"
import type { RunSnapshot } from "@/lib/live/runApi"
import { layoutGraph, type GraphLayout, type GraphPosition } from "./layout"

const states = [
  "Pending",
  "Ready",
  "Dispatched",
  "Running",
  "Retrying",
  "Succeeded",
  "SoftFailed",
  "Failed",
  "TimedOut",
  "Cancelled",
  "Skipped",
  "Reused",
]

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

const GraphButton = memo(function GraphButton({
  position,
  status,
  selected,
  onSelect,
}: {
  position: GraphPosition
  status: number
  selected: boolean
  onSelect: (key: string) => void
}) {
  return (
    <button
      type="button"
      data-graph-node={position.key}
      data-state={status}
      aria-label={`${position.key}: ${states[status]}`}
      aria-pressed={selected}
      title={`${position.key}: ${states[status]}`}
      style={position.style}
      className={`absolute flex flex-col justify-center rounded-md border bg-card px-3 text-left text-card-foreground [contain:layout_paint] focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none ${selected ? "ring-2 ring-ring" : ""}`}
      onClick={() => onSelect(position.key)}
    >
      <span className="truncate font-medium">{position.key}</span>
      <span className="text-xs text-muted-foreground">{states[status]}</span>
    </button>
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
  if (
    plan.versionId !== run.versionId ||
    plan.workflowId !== run.workflowId ||
    Object.keys(run.nodes).length !== plan.nodes.length ||
    plan.nodes.some(
      (node) =>
        !Object.hasOwn(run.nodes, node.key) ||
        states[run.nodes[node.key]!] === undefined
    )
  )
    throw new Error(
      "The pinned manifest and authoritative run graph do not match."
    )
  const node = plan.nodes.find((node) => node.key === selected)
  const [team = "", type = "", wf = ""] = plan.workflowId.split("/")
  return (
    <section aria-label="Live run graph" className="min-w-0">
      <h2 className="mb-2 text-xl font-semibold">Dependency graph</h2>
      <p className="mb-3 text-sm text-muted-foreground">
        Select a node to inspect its tasks and historical Task Plan. Scroll to
        explore larger graphs.
      </p>
      {layout.nodes.length === 0 ? (
        <p>No declared nodes.</p>
      ) : (
        <div
          role="region"
          aria-label="Run dependency graph"
          tabIndex={0}
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
              <GraphButton
                key={position.key}
                position={position}
                status={run.nodes[position.key]!}
                selected={selected === position.key}
                onSelect={select}
              />
            ))}
          </div>
        </div>
      )}
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
