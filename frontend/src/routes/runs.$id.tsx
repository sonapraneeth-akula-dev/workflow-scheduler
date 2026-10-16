import { createFileRoute, Link } from "@tanstack/react-router"
import { z } from "zod"
import { ReconnectingIndicator } from "@/components/ReconnectingIndicator"
import { LiveRunGraph } from "@/features/graph/RunGraph"
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
          <Link
            to="/t/$team/$type/w/$wf/versions/$v"
            params={{ team, type, wf, v: run.versionId }}
          >
            Task Plan for this run
          </Link>
          <p>Committed sequence: {run.lastSeq}</p>
          <LiveRunGraph run={run} />
        </>
      ) : null}
    </>
  )
}
