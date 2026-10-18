import { createFileRoute, Link } from "@tanstack/react-router"
import { z } from "zod"
import { Tabs } from "radix-ui"
import { ReconnectingIndicator } from "@/components/ReconnectingIndicator"
import { LiveRunGraph } from "@/features/graph/RunGraph"
import { AbortDialog } from "@/features/runs/AbortDialog"
import { RerunDialog } from "@/features/runs/RerunDialog"
import { RunLineage } from "@/features/runs/RunLineage"
import { Timeline } from "@/features/runs/Timeline"
import { SystemEvents } from "@/features/runs/SystemEvents"
import { ResourceCharts } from "@/features/runs/ResourceCharts"
import { TaskOutputs } from "@/features/runs/TaskOutputs"
import { TaskLogs } from "@/features/runs/TaskLogs"
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
    <div className="min-w-0 space-y-4">
      <section
        aria-label="Run summary"
        className="min-w-0 space-y-2 border bg-card p-3"
      >
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0 space-y-1">
            <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
              <h1 className="text-2xl font-semibold">Run</h1>
              {run ? (
                <p className="font-medium break-words">{run.workflowId}</p>
              ) : null}
            </div>
            <p className="font-mono text-xs break-all text-muted-foreground">
              {id}
            </p>
          </div>
          {run ? (
            <p className="text-xs text-muted-foreground">
              Committed sequence:{" "}
              <span className="font-mono text-foreground">{run.lastSeq}</span>
            </p>
          ) : null}
        </div>
        <ReconnectingIndicator {...live} />
        {run ? (
          <div className="flex flex-wrap items-start justify-between gap-x-6 gap-y-3">
            <div className="flex min-w-0 flex-wrap items-start gap-3 text-sm">
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
                placement={run.placement}
                live={live.status === "live"}
              />
            </div>
            <nav
              aria-label="Related run pages"
              className="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm [&_a]:inline-flex [&_a]:min-h-8 [&_a]:items-center [&_a]:text-primary [&_a]:underline [&_a]:underline-offset-4"
            >
              <Link
                to="/t/$team/$type/w/$wf/versions/$v"
                params={{ team, type, wf, v: run.versionId }}
              >
                Task Plan for this run
              </Link>
              <Link
                to="/t/$team/$type/w/$wf/history"
                params={{ team, type, wf }}
              >
                Workflow history
              </Link>
              {run.seriesId ? (
                <Link to="/series/$id" params={{ id: run.seriesId }}>
                  Series history
                </Link>
              ) : null}
            </nav>
          </div>
        ) : null}
      </section>
      {run ? (
        <>
          {run.placement?.singleMachine ? (
            <p
              className="w-fit rounded border px-2 py-1 text-sm"
              aria-label="Run placement override"
            >
              Single machine: {run.placement.machine ?? "auto-picked"}
              {run.placementAgentId
                ? ` (${run.placementAgentId})`
                : " (waiting for first dispatch)"}
            </p>
          ) : null}
          {Object.entries(run.taskWaitReasons ?? {}).map(([task, reason]) => (
            <p key={task} role="status" className="break-all">
              {task}: {reason} - waiting for eligible machine capacity.
            </p>
          ))}
          <QueuedBanner
            key={run.runId}
            runId={run.runId}
            active={run.status <= 1}
          />
          <RunLineage run={run} />
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
                value="logs"
                className="rounded border px-3 py-2 data-[state=active]:bg-muted"
              >
                Logs
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
              <Tabs.Trigger
                value="events"
                className="rounded border px-3 py-2 data-[state=active]:bg-muted"
              >
                System Events
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
            <Tabs.Content value="logs" className="min-w-0">
              <TaskLogs key={run.runId} runId={run.runId} />
            </Tabs.Content>
            <Tabs.Content value="events" className="min-w-0 space-y-4">
              <Link to="/runs/$id/events" params={{ id: run.runId }}>
                Open System Events page
              </Link>
              <SystemEvents key={run.runId} runId={run.runId} />
            </Tabs.Content>
          </Tabs.Root>
        </>
      ) : null}
    </div>
  )
}
