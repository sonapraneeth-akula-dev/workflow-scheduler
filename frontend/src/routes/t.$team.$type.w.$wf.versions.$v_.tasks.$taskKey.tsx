import { createFileRoute } from "@tanstack/react-router"
import { z } from "zod"
import { TaskPlanPage } from "@/features/taskplan/TaskPlanPage"

export const Route = createFileRoute(
  "/t/$team/$type/w/$wf/versions/$v_/tasks/$taskKey"
)({
  params: {
    parse: (params) => ({
      ...params,
      v: z.uuid().parse(params.v),
      taskKey: z
        .string()
        .regex(/^[^/]+\/[^/]+$/)
        .parse(params.taskKey),
    }),
  },
  component: VersionTask,
})

function VersionTask() {
  const { team, type, wf, v, taskKey } = Route.useParams()
  return (
    <TaskPlanPage
      workflowId={`${team}/${type}/${wf}`}
      versionId={v}
      taskKey={taskKey}
    />
  )
}
