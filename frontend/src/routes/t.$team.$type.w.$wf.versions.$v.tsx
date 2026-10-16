import { createFileRoute } from "@tanstack/react-router"
import { z } from "zod"
import { TaskPlanPage } from "@/features/taskplan/TaskPlanPage"
import { TriggerDialog } from "@/features/runs/TriggerDialog"

export const Route = createFileRoute("/t/$team/$type/w/$wf/versions/$v")({
  params: { parse: (params) => ({ ...params, v: z.uuid().parse(params.v) }) },
  component: VersionPlan,
})

function VersionPlan() {
  const { team, type, wf, v } = Route.useParams()
  const workflowId = `${team}/${type}/${wf}`
  return (
    <>
      <TriggerDialog workflowId={workflowId} versionId={v} />
      <TaskPlanPage workflowId={workflowId} versionId={v} />
    </>
  )
}
