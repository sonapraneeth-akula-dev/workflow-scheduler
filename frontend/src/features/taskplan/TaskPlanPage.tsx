import { useQuery } from "@tanstack/react-query"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
import { getVersionPlan, type PlanPlacement } from "./api"

function selector(value: { literal: string | null; variable: string | null }) {
  return value.literal ?? `variable(${value.variable})`
}

export function describePlacement(placement: PlanPlacement): string {
  const parts: string[] = []
  const selected = placement?.selector
  if (selected?.os) parts.push(`OS: ${selector(selected.os)}`)
  if (selected?.pool) parts.push(`Pool: ${selector(selected.pool)}`)
  for (const label of selected?.labels ?? [])
    parts.push(
      `Label: ${label.key}${label.value ? `=${selector(label.value)}` : ""}`
    )
  if (selected?.machines.length)
    parts.push(`Machines: ${selected.machines.map(selector).join(", ")}`)
  if (placement?.affinity && placement.affinity.kind !== "None")
    parts.push(
      `Affinity: ${placement.affinity.kind}${placement.affinity.nodeKey ? ` (${placement.affinity.nodeKey})` : ""}`
    )
  return parts.join("; ") || "Any eligible agent"
}

export function TaskPlanPage({
  workflowId,
  versionId,
  taskKey,
}: {
  workflowId: string
  versionId: string
  taskKey?: string
}) {
  const query = useQuery({
    queryKey: ["version-plan", workflowId, versionId],
    queryFn: ({ signal }) => getVersionPlan(workflowId, versionId, signal),
  })
  const plan = query.error ? undefined : query.data
  const selected = taskKey
    ? plan?.nodes
        .flatMap((node) => node.tasks.map((task) => `${node.key}/${task.key}`))
        .includes(taskKey)
    : true
  return (
    <>
      <h1 className="text-2xl font-semibold">Task Plan</h1>
      <p className="break-words">{workflowId}</p>
      <p className="break-all">Pinned version: {versionId}</p>
      <p>
        Read-only declared actions, not a prediction of commands generated
        inside SDK functions. Command arguments, environment, HTTP credentials,
        headers, bodies and variable values are omitted.
      </p>
      {query.isPending ? (
        <p role="status">Loading historical task plan...</p>
      ) : null}
      {query.error ? (
        <>
          <ErrorNotice error={query.error} />
          <Button variant="outline" onClick={() => void query.refetch()}>
            Retry task plan
          </Button>
        </>
      ) : null}
      {plan ? (
        <>
          <p>
            Version {plan.version}; run timeout: {plan.runTimeout}.
          </p>
          <p>Workflow placement: {describePlacement(plan.placement)}.</p>
          {taskKey && !selected ? (
            <p role="alert">This task is not part of the pinned version.</p>
          ) : null}
          {plan.nodes.length === 0 ? <p>No declared nodes.</p> : null}
          {plan.nodes.map((node) => (
            <section
              key={node.key}
              aria-label={`Node ${node.key}`}
              className="min-w-0 rounded-md border p-4"
            >
              <h2 className="text-xl font-semibold break-words">{node.key}</h2>
              <p>
                {plan.stages.includes(node.key) ? "Lifecycle stage. " : ""}
                {node.mode} task order
                {node.maxParallel
                  ? `; concurrency: ${node.maxParallel}`
                  : ""}. Failure: {node.failure}; node timeout:{" "}
                {node.timeout ?? "run deadline"}.
              </p>
              <p>Planned placement: {describePlacement(node.placement)}.</p>
              <p>
                Dependencies:{" "}
                {node.dependencies.length
                  ? node.dependencies
                      .map(
                        (dependency) =>
                          `${dependency.node} (${dependency.when})`
                      )
                      .join(", ")
                  : "None"}
                .
              </p>
              <ol className="mt-4 flex list-inside list-decimal flex-col gap-4">
                {node.tasks.map((task) => (
                  <li
                    key={task.key}
                    id={`task-${encodeURIComponent(`${node.key}/${task.key}`)}`}
                    aria-current={
                      taskKey === `${node.key}/${task.key}` ? "true" : undefined
                    }
                    className="min-w-0 rounded border p-3 break-words"
                  >
                    <h3 className="inline font-semibold">{task.key}</h3>
                    <p>
                      {task.action
                        ? `${task.action.kind}: ${task.action.declaration}`
                        : "Declaration unavailable in this historical manifest."}
                    </p>
                    <p>
                      Planned placement: {describePlacement(task.placement)}.
                    </p>
                    <p>
                      Attempt timeout: {task.policy.attemptTimeout}; failure:{" "}
                      {task.failure}.
                    </p>
                    <p>
                      Automatic retries:{" "}
                      {task.policy.autoRetry ? "enabled" : "disabled"}; retry
                      budget: {task.policy.retry.retries}; categories:{" "}
                      {task.policy.retry.on.join(", ") || "None"}; backoff:{" "}
                      {task.policy.retry.backoff.kind}{" "}
                      {task.policy.retry.backoff.delay} (maximum{" "}
                      {task.policy.retry.backoff.maxDelay}).
                    </p>
                    {task.requestTimeout ? (
                      <p>HTTP request timeout: {task.requestTimeout}.</p>
                    ) : null}
                    {task.secretReference ? (
                      <p>
                        Secret reference: {task.secretReference} (value never
                        displayed).
                      </p>
                    ) : null}
                  </li>
                ))}
              </ol>
            </section>
          ))}
        </>
      ) : null}
    </>
  )
}
