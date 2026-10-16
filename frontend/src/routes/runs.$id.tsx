import { createFileRoute, Link } from "@tanstack/react-router"
import { z } from "zod"
import { ReconnectingIndicator } from "@/components/ReconnectingIndicator"
import { LiveRunGraph } from "@/features/graph/RunGraph"
import { AbortDialog } from "@/features/runs/AbortDialog"
import { useRun } from "@/lib/live/useRun"

export const Route = createFileRoute("/runs/$id")({
  params: { parse: (params) => ({ id: z.uuid().parse(params.id) }) },
  component: RunDetail,
})

function RunDetail() {
  const { id } = Route.useParams()
  const live = useRun(id)
  const run = live.run
  const [team = "", type = "", wf = ""] = run?.workflowId.split("/") ?? []
  return (
    <>
      <h1 className="text-2xl font-semibold">Run</h1>
      <p className="break-all">{id}</p>
      <ReconnectingIndicator {...live} />
      {run ? (
        <>
          <p className="break-words">{run.workflowId}</p>
          <AbortDialog
            key={run.runId}
            runId={run.runId}
            status={run.status}
            abort={run.abort}
            live={live.status === "live"}
          />
          <Link
            to="/t/$team/$type/w/$wf/versions/$v"
            params={{ team, type, wf, v: run.versionId }}
          >
            Task Plan for this run
          </Link>
          <Link to="/t/$team/$type/w/$wf/history" params={{ team, type, wf }}>
            Workflow history
          </Link>
          {run.seriesId ? (
            <Link to="/series/$id" params={{ id: run.seriesId }}>
              Series history
            </Link>
          ) : null}
          <p>Committed sequence: {run.lastSeq}</p>
          <LiveRunGraph run={run} />
        </>
      ) : null}
    </>
  )
}
