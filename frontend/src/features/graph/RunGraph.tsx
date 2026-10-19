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
import { CircleDashedIcon, RotateCcwIcon } from "lucide-react"
import { ErrorNotice } from "@/components/ErrorNotice"
import { StatusBadge } from "@/components/StatusBadge"
import { Button } from "@/components/ui/button"
import { getVersionPlan, type VersionPlan } from "@/features/taskplan/api"
import { TaskOutputs } from "@/features/runs/TaskOutputs"
import { getTimeline, type TimelineAttempt } from "@/features/runs/timeline-api"
import { executionStatuses, type RunSnapshot } from "@/lib/live/runApi"
import { GraphNode, NodePolicyIcons } from "./GraphNode"
import { layoutGraph, type GraphLayout } from "./layout"
import { describeNodeRetries, retryableFailures } from "./policies"

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
      className="pointer-events-none absolute top-0 left-0 text-primary will-change-transform"
    >
      <defs>
        <marker
          id={marker}
          viewBox="0 0 8 10"
          markerUnits="userSpaceOnUse"
          markerWidth="8"
          markerHeight="10"
          refX="6"
          refY="5"
          orient="auto"
        >
          <path
            d="M1 1 L6 5 L1 9"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        </marker>
      </defs>
      {layout.edges.map((edge) => (
        <path
          key={`${edge.from}/${edge.to}`}
          data-graph-edge={`${edge.from}/${edge.to}`}
          d={edge.path}
          fill="none"
          stroke="currentColor"
          strokeWidth="2"
          strokeLinecap="round"
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
  attempts,
}: {
  plan: VersionPlan
  run: RunSnapshot
  attempts?: TimelineAttempt[]
}) {
  const layout = useMemo(() => layoutGraph(plan.nodes), [plan.nodes])
  const retryCounts = useMemo(() => {
    if (attempts === undefined) return undefined
    const counts = new Map<string, number>()
    const tasks = new Set<string>()
    for (const attempt of attempts) {
      if (tasks.has(attempt.taskKey))
        counts.set(attempt.nodeKey, (counts.get(attempt.nodeKey) ?? 0) + 1)
      tasks.add(attempt.taskKey)
    }
    return counts
  }, [attempts])
  const policies = useMemo(
    () =>
      new Map(
        plan.nodes.map((node) => [
          node.key,
          {
            optional: node.failure === "Soft",
            retries: describeNodeRetries(node),
            taskCount: node.tasks.length,
            mode: node.mode,
          },
        ])
      ),
    [plan.nodes]
  )
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
    <section
      aria-label="Live run graph"
      className={`grid min-w-0 gap-x-4 gap-y-3 ${node ? "xl:grid-cols-[minmax(0,1fr)_20rem]" : ""}`}
    >
      <div className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1 xl:col-span-full">
        <h2 className="text-lg font-semibold">
          Dependency graph{" "}
          <span className="ml-2 text-xs font-normal text-muted-foreground">
            {layout.nodes.length} nodes / {layout.edges.length} edges
          </span>
        </h2>
        <p id={instructions} className="text-xs text-muted-foreground">
          Arrow keys: move. Enter: inspect.
          <span className="sr-only">
            {" "}
            Space also inspects tasks and the historical Task Plan. Home/End
            reaches the first/last node. Tab leaves the graph. Scroll to
            explore. The node list offers the same information without spatial
            navigation.
          </span>
        </p>
      </div>
      <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-muted-foreground xl:col-span-full">
        <span>Nodes group tasks. Select to inspect.</span>
        <span className="inline-flex items-center gap-1">
          <CircleDashedIcon aria-hidden="true" className="size-3.5" />
          Optional (soft failure)
        </span>
        <span className="inline-flex items-center gap-1">
          <RotateCcwIcon aria-hidden="true" className="size-3.5" />
          Retries (failure or platform; hover for policy)
        </span>
        <span>Numbered retry icon: recorded retries after completion.</span>
      </div>
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
                {...policies.get(position.key)!}
                retryCount={
                  retryCounts ? (retryCounts.get(position.key) ?? 0) : undefined
                }
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
          className="min-w-0 text-sm xl:col-start-1"
          onToggle={(event) => setListOpen(event.currentTarget.open)}
        >
          <summary className="cursor-pointer py-1 font-medium">
            Node list
          </summary>
          {listOpen ? (
            <div
              role="region"
              aria-label="Scrollable node table"
              tabIndex={0}
              className="mt-2 overflow-x-auto border bg-card"
            >
              <table
                aria-label="Run nodes"
                className="w-full min-w-[48rem] text-left"
              >
                <thead className="border-b bg-secondary text-secondary-foreground">
                  <tr>
                    {[
                      "Node",
                      "Status",
                      "Tasks",
                      "Policies",
                      "Dependencies",
                    ].map((label) => (
                      <th
                        scope="col"
                        key={label}
                        className="px-3 py-2 font-bold"
                      >
                        {label}
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {plan.nodes.map((item) => (
                    <tr
                      key={item.key}
                      data-selected={selected === item.key ? true : undefined}
                      className="border-t odd:bg-card even:bg-muted/50 hover:bg-accent/40 data-[selected=true]:bg-secondary"
                    >
                      <th
                        scope="row"
                        className="w-1/4 px-3 py-2 font-normal [overflow-wrap:anywhere]"
                      >
                        <button
                          type="button"
                          aria-label={`Inspect node ${item.key}`}
                          aria-pressed={selected === item.key}
                          className="font-medium text-primary underline underline-offset-4 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring"
                          onClick={() => select(item.key)}
                        >
                          {item.key}
                        </button>
                      </th>
                      <td className="px-3 py-2 whitespace-nowrap">
                        <StatusBadge
                          status={executionStatuses[run.nodes[item.key]!]!}
                        />
                      </td>
                      <td className="px-3 py-2 whitespace-nowrap">
                        {item.tasks.length} (
                        {item.mode === "Series" ? "sequential" : "parallel"})
                      </td>
                      <td className="relative px-3 py-2">
                        <NodePolicyIcons
                          optional={item.failure === "Soft"}
                          retries={policies.get(item.key)!.retries}
                          retryCount={
                            retryCounts
                              ? (retryCounts.get(item.key) ?? 0)
                              : undefined
                          }
                        />
                        <span className="sr-only">
                          {item.failure === "Soft"
                            ? "Optional node (soft failure)."
                            : "Required node (hard failure)."}{" "}
                          {policies.get(item.key)!.retries ??
                            "No retries configured."}
                          {retryCounts
                            ? ` Recorded retries: ${retryCounts.get(item.key) ?? 0}.`
                            : ""}
                        </span>
                      </td>
                      <td className="px-3 py-2 [overflow-wrap:anywhere]">
                        {item.dependencies
                          .map(
                            (dependency) =>
                              `${dependency.node} (${dependency.when})`
                          )
                          .join(", ") || "None"}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : null}
        </details>
      ) : null}
      {node ? (
        <section
          aria-label={`Selected node ${node.key}`}
          className="min-w-0 self-start border bg-card p-3 text-sm break-words xl:col-start-2 xl:row-span-2 xl:row-start-3"
        >
          <h3 className="font-semibold">Node: {node.key}</h3>
          <p className="text-muted-foreground">
            {node.tasks.length} {node.tasks.length === 1 ? "task" : "tasks"}{" "}
            {node.tasks.length === 1 ? "runs" : "run"}{" "}
            {node.mode === "Series"
              ? "sequentially, in the order below."
              : `in parallel${node.maxParallel ? `, up to ${node.maxParallel} at a time` : ""}.`}
          </p>
          <p>
            {node.failure === "Soft"
              ? "Optional node: soft-failure policy; still runs."
              : "Required node: hard-failure policy."}
          </p>
          {retryCounts ? (
            <p>Recorded retries: {retryCounts.get(node.key) ?? 0}.</p>
          ) : null}
          {run.reusedNodes[node.key] ? (
            <p className="break-all">
              <StatusBadge status="Reused" />{" "}
              <Link to="/runs/$id" params={{ id: run.reusedNodes[node.key]! }}>
                Original outputs and attempts for {node.key}
              </Link>{" "}
              No original attempt data is substituted into this run.
            </p>
          ) : null}
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
            <div className="mt-3">
              <h4 className="font-semibold">Tasks ({node.tasks.length})</h4>
              <p className="text-xs text-muted-foreground">
                Task plans show configuration, not task outputs. Retries depend
                on failure category, remaining budget and deadline.
              </p>
              <ol
                aria-label={`Tasks in node ${node.key}`}
                className="mt-2 flex list-inside list-decimal flex-col gap-3"
              >
                {node.tasks.map((task) => {
                  const status = run.tasks[`${node.key}/${task.key}`]
                  const retryOn = retryableFailures(task)
                  return (
                    <li key={task.key}>
                      <span className="font-medium">{task.key}</span>{" "}
                      {status === undefined ? (
                        <span className="text-xs text-muted-foreground">
                          Status not recorded
                        </span>
                      ) : (
                        <StatusBadge status={executionStatuses[status]!} />
                      )}
                      {task.failure === "Soft" ? (
                        <p>Soft-failure task.</p>
                      ) : null}
                      <p className="mt-1 text-xs text-muted-foreground">
                        Failure retries:{" "}
                        {retryOn.length
                          ? `up to ${task.policy.retry.retries} (${retryOn.join(", ")})`
                          : "disabled"}
                        . Platform recovery:{" "}
                        {task.policy.autoRetry
                          ? "enabled for agent/platform failures"
                          : "disabled"}
                        .
                      </p>
                      {run.taskWaitReasons?.[`${node.key}/${task.key}`] ? (
                        <span role="status">
                          WaitingForMachine: no eligible machine has available
                          capacity.{" "}
                        </span>
                      ) : null}
                      <Link
                        to="/t/$team/$type/w/$wf/versions/$v/tasks/$taskKey"
                        params={{
                          team,
                          type,
                          wf,
                          v: plan.versionId,
                          taskKey: `${node.key}/${task.key}`,
                        }}
                        aria-label={`View task plan for ${task.key}`}
                        className="text-xs text-primary underline underline-offset-4"
                      >
                        View task plan
                      </Link>
                    </li>
                  )
                })}
              </ol>
            </div>
          )}
          {!run.reusedNodes[node.key] && node.tasks.length > 0 ? (
            <NodeDataflow
              key={`${run.runId}/${node.key}`}
              runId={run.runId}
              nodeKey={node.key}
            />
          ) : null}
        </section>
      ) : null}
    </section>
  )
}

function NodeDataflow({ runId, nodeKey }: { runId: string; nodeKey: string }) {
  const [open, setOpen] = useState(false)
  return (
    <details
      className="mt-4"
      onToggle={(event) => setOpen(event.currentTarget.open)}
    >
      <summary className="cursor-pointer font-semibold">
        Inspect recorded inputs / outputs
      </summary>
      {open ? (
        <div className="mt-3">
          <TaskOutputs runId={runId} nodeKey={nodeKey} />
        </div>
      ) : null}
    </details>
  )
}

export function LiveRunGraph({ run }: { run: RunSnapshot }) {
  const query = useQuery({
    queryKey: ["version-plan", run.workflowId, run.versionId],
    queryFn: ({ signal }) =>
      getVersionPlan(run.workflowId, run.versionId, signal),
  })
  const history = useQuery({
    queryKey: ["graph-attempts", run.runId],
    enabled: run.status >= 3,
    queryFn: async ({ signal }) => {
      const timeline = await getTimeline(run.runId, signal)
      if (
        timeline.lastSeq < run.lastSeq ||
        !["Succeeded", "Failed", "Aborted", "TimedOut"].includes(
          timeline.status
        )
      )
        throw new Error(
          "The committed attempt history has not reached the terminal run state."
        )
      return timeline.attempts
    },
    staleTime: Infinity,
    gcTime: 0,
    retry: false,
    refetchOnWindowFocus: false,
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
  return (
    <>
      {run.status >= 3 && history.isPending ? (
        <p role="status">Loading recorded retry history...</p>
      ) : null}
      {history.error ? (
        <div>
          <ErrorNotice error={history.error} />
          <Button variant="outline" onClick={() => void history.refetch()}>
            Retry recorded attempt history
          </Button>
        </div>
      ) : null}
      <RunGraph
        key={run.runId}
        plan={query.data}
        run={run}
        attempts={
          history.isSuccess && !history.isFetching ? history.data : undefined
        }
      />
    </>
  )
}
