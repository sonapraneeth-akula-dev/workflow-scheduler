import { createFileRoute, Link } from "@tanstack/react-router"
import { z } from "zod"
import { Tabs } from "radix-ui"
import { ReconnectingIndicator } from "@/components/ReconnectingIndicator"
import { LiveRunGraph } from "@/features/graph/RunGraph"
import { AbortDialog } from "@/features/runs/AbortDialog"
import { RerunDialog } from "@/features/runs/RerunDialog"
import { RunLineage } from "@/features/runs/RunLineage"
import { Timeline } from "@/features/runs/Timeline"
import { ResourceCharts } from "@/features/runs/ResourceCharts"
import { TaskOutputs } from "@/features/runs/TaskOutputs"
import { QueuedBanner } from "@/features/runs/QueuedBanner"
import { ReportIndex } from "@/features/reports/ReportIndex"
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
          <QueuedBanner
            key={run.runId}
            runId={run.runId}
            active={run.status <= 1}
          />
          <AbortDialog
            key={run.runId}
            runId={run.runId}
            status={run.status}
            abort={run.abort}
            live={live.status === "live"}
          />
          <RerunDialog
            key={`rerun-${run.runId}`}
            runId={run.runId}
            versionId={run.versionId}
            status={run.status}
            live={live.status === "live"}
          />
          <RunLineage run={run} />
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
          <Tabs.Root defaultValue="graph" className="min-w-0 space-y-4">
            <Tabs.List aria-label="Run views" className="flex flex-wrap gap-2">
              <Tabs.Trigger
                value="graph"
                className="rounded border px-3 py-2 data-[state=active]:bg-muted"
              >
                Graph
              </Tabs.Trigger>
              <Tabs.Trigger
                value="timeline"
                className="rounded border px-3 py-2 data-[state=active]:bg-muted"
              >
                Timeline
              </Tabs.Trigger>
              <Tabs.Trigger
                value="metrics"
                className="rounded border px-3 py-2 data-[state=active]:bg-muted"
              >
                Metrics
              </Tabs.Trigger>
              <Tabs.Trigger
                value="outputs"
                className="rounded border px-3 py-2 data-[state=active]:bg-muted"
              >
                Outputs
              </Tabs.Trigger>
              <Tabs.Trigger
                value="reports"
                className="rounded border px-3 py-2 data-[state=active]:bg-muted"
              >
                Reports
              </Tabs.Trigger>
            </Tabs.List>
            <Tabs.Content
              value="graph"
              forceMount
              className="min-w-0 data-[state=inactive]:hidden"
            >
              <LiveRunGraph run={run} />
            </Tabs.Content>
            <Tabs.Content value="timeline" className="min-w-0">
              <Timeline runId={run.runId} />
            </Tabs.Content>
            <Tabs.Content value="reports" className="min-w-0">
              <ReportIndex key={run.runId} runId={run.runId} />
            </Tabs.Content>
            <Tabs.Content value="metrics" className="min-w-0">
              <ResourceCharts key={run.runId} runId={run.runId} />
            </Tabs.Content>
            <Tabs.Content value="outputs" className="min-w-0">
              <TaskOutputs key={run.runId} runId={run.runId} />
            </Tabs.Content>
          </Tabs.Root>
        </>
      ) : null}
    </>
  )
}
