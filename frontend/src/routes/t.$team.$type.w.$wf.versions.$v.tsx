import { createFileRoute } from "@tanstack/react-router"
import { z } from "zod"
import { TaskPlanPage } from "@/features/taskplan/TaskPlanPage"

export const Route = createFileRoute("/t/$team/$type/w/$wf/versions/$v")({
  params: { parse: (params) => ({ ...params, v: z.uuid().parse(params.v) }) },
  component: VersionPlan,
})

function VersionPlan() {
  const { team, type, wf, v } = Route.useParams()
  return <TaskPlanPage workflowId={`${team}/${type}/${wf}`} versionId={v} />
}
