import { createFileRoute } from "@tanstack/react-router"
import { VersionsTab } from "@/features/versions/VersionsTab"

export const Route = createFileRoute("/t/$team/$type/w/$wf/versions/")({
  component: WorkflowVersions,
})

function WorkflowVersions() {
  const { team, type, wf } = Route.useParams()
  return (
    <VersionsTab
      key={`${team}/${type}/${wf}`}
      workflowId={`${team}/${type}/${wf}`}
    />
  )
}
