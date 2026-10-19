import type { VersionPlan } from "@/features/taskplan/api"

type Node = VersionPlan["nodes"][number]
type Task = Node["tasks"][number]

export function retryableFailures(task: Task) {
  return task.policy.retry.retries > 0
    ? task.policy.retry.on.filter((category) =>
        ["TaskError", "Timeout", "AgentLost", "PlatformError"].includes(
          category
        )
      )
    : []
}

export function describeNodeRetries(node: Node) {
  const failures = node.tasks.filter(
    (task) => retryableFailures(task).length > 0
  ).length
  const platform = node.tasks.filter((task) => task.policy.autoRetry).length
  return failures || platform
    ? `Failure retries: ${failures}/${node.tasks.length} tasks; platform recovery: ${platform}/${node.tasks.length} tasks.`
    : null
}
