import { createFileRoute, Link } from "@tanstack/react-router"
import { z } from "zod"
import { TaskPlanPage } from "@/features/taskplan/TaskPlanPage"
import { TriggerDialog } from "@/features/runs/TriggerDialog"
import { VersionsTab } from "@/features/versions/VersionsTab"

export const Route = createFileRoute("/t/$team/$type/w/$wf/versions/$v")({
  params: { parse: (params) => ({ ...params, v: z.uuid().parse(params.v) }) },
  component: VersionPlan,
})

function VersionPlan() {
  const { team, type, wf, v } = Route.useParams()
  const workflowId = `${team}/${type}/${wf}`
  return (
    <>
      <Link to="/t/$team/$type/w/$wf/versions" params={{ team, type, wf }}>
        All workflow versions
      </Link>
      <TriggerDialog workflowId={workflowId} versionId={v} />
      <TaskPlanPage workflowId={workflowId} versionId={v} />
      <VersionsTab
        key={`${workflowId}/${v}`}
        workflowId={workflowId}
        pinnedVersionId={v}
      />
    </>
  )
}
