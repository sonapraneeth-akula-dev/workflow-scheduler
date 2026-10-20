import {
  createFileRoute,
  Link,
  useCanGoBack,
  useRouter,
} from "@tanstack/react-router"
import { z } from "zod"
import { Tabs } from "radix-ui"
import { ArrowLeftIcon } from "lucide-react"
import { ReconnectingIndicator } from "@/components/ReconnectingIndicator"
import { Button } from "@/components/ui/button"
import { LiveRunGraph } from "@/features/graph/RunGraph"
import { AbortDialog } from "@/features/runs/AbortDialog"
import { RerunDialog } from "@/features/runs/RerunDialog"
import { RunLineage } from "@/features/runs/RunLineage"
import { Timeline } from "@/features/runs/Timeline"
import { SystemEvents } from "@/features/runs/SystemEvents"
import { ResourceCharts } from "@/features/runs/ResourceCharts"
import { TaskOutputs } from "@/features/runs/TaskOutputs"
import { RunFailures } from "@/features/runs/RunFailures"
import { TaskLogs } from "@/features/runs/TaskLogs"
import { QueuedBanner } from "@/features/runs/QueuedBanner"
import { ReportIndex } from "@/features/reports/ReportIndex"
import { useRun } from "@/lib/live/useRun"

const runTabClassName =
  "inline-flex h-10 shrink-0 items-center justify-center border-b-2 border-transparent px-4 text-sm font-medium whitespace-nowrap text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-ring data-[state=active]:border-primary data-[state=active]:bg-secondary data-[state=active]:font-semibold data-[state=active]:text-secondary-foreground"

export const Route = createFileRoute("/runs/$id")({
  params: { parse: (params) => ({ id: z.uuid().parse(params.id) }) },
  component: RunDetail,
})

function RunDetail() {
  const { id } = Route.useParams()
  const router = useRouter()
  const canGoBack = useCanGoBack()
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
              <Button
                variant="ghost"
                size="sm"
                aria-label={
                  canGoBack ? "Back to previous page" : "Back to runs"
                }
                onClick={() => {
                  if (canGoBack) router.history.back()
                  else void router.navigate({ to: "/runs", replace: true })
                }}
              >
                <ArrowLeftIcon aria-hidden="true" />
                Back
              </Button>
              <h1 className="text-2xl font-semibold">Run</h1>
              {run ? (
                <p className="min-w-0 font-medium [overflow-wrap:anywhere]">
                  {run.workflowId}
                </p>
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
          <RunFailures key={run.runId} run={run} />
          <Tabs.Root defaultValue="graph" className="min-w-0 space-y-3">
            <Tabs.List
              aria-label="Run views"
              className="sticky top-0 z-20 flex w-full min-w-0 flex-nowrap gap-1 overflow-x-auto border-b bg-card p-1 shadow-sm"
            >
              <Tabs.Trigger value="graph" className={runTabClassName}>
                Graph
              </Tabs.Trigger>
              <Tabs.Trigger value="timeline" className={runTabClassName}>
                Timeline
              </Tabs.Trigger>
              <Tabs.Trigger value="metrics" className={runTabClassName}>
                Metrics
              </Tabs.Trigger>
              <Tabs.Trigger value="logs" className={runTabClassName}>
                Logs
              </Tabs.Trigger>
              <Tabs.Trigger value="outputs" className={runTabClassName}>
                Outputs
              </Tabs.Trigger>
              <Tabs.Trigger value="reports" className={runTabClassName}>
                Reports
              </Tabs.Trigger>
              <Tabs.Trigger value="events" className={runTabClassName}>
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
