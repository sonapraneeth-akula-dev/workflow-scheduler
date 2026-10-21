import { useEffect, useState } from "react"
import { useQuery } from "@tanstack/react-query"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
import { ApiError } from "@/lib/api/client"
import { usePermissions } from "@/lib/auth/usePermissions"
import { formatTimestamp } from "@/lib/timestamps"
import {
  getDeploymentHistory,
  getDeploymentSnapshot,
  type DeploymentHistory,
  type DeploymentSnapshot,
} from "./api"

const stateLabel = {
  InSync: "In sync",
  AwaitingReplacement: "Awaiting replacement",
  Updating: "Updating",
  Blocked: "Blocked",
  Unknown: "Unknown",
}

function Conditions({
  rows,
}: {
  rows: { type: string; status: string; reason: string | null }[]
}) {
  return rows.length ? (
    <ul className="text-sm text-muted-foreground">
      {rows.map((row) => (
        <li key={row.type}>
          {row.type}: {row.status}
          {row.reason ? ` (${row.reason})` : ""}
        </li>
      ))}
    </ul>
  ) : null
}

function ActualRollout({ snapshot }: { snapshot: DeploymentSnapshot }) {
  return (
    <div className="space-y-4">
      <p className="text-sm text-muted-foreground">
        Namespace: {snapshot.namespace}. Observed{" "}
        <time dateTime={snapshot.observedAt}>
          {formatTimestamp(snapshot.observedAt)}
        </time>
        . Kubernetes resources are read separately, not as an atomic cluster
        snapshot.
      </p>
      <div className="grid min-w-0 gap-3 xl:grid-cols-2">
        {snapshot.workloads.map((row) => (
          <article
            key={row.name}
            aria-label={row.name}
            className="min-w-0 space-y-3 border bg-card p-4"
          >
            <h3 className="font-semibold break-all">{row.name}</h3>
            <p>
              <strong>{stateLabel[row.state]}</strong> · {row.kind} ·{" "}
              {row.strategy}
            </p>
            <p className="text-sm">
              Desired {row.desired} · Updated {row.updated} · Ready {row.ready}{" "}
              · Available {row.available} · Matching target images{" "}
              {row.matchingImages}
            </p>
            <p className="text-sm text-muted-foreground">
              Generation {row.generation}; observed{" "}
              {row.observedGeneration ?? "not yet reported"}.
            </p>
            {row.state === "AwaitingReplacement" ? (
              <p className="text-sm text-warning">
                OnDelete retains old pods until explicit replacement. Ready does
                not mean the new version is running.
              </p>
            ) : null}
            <dl className="space-y-1 text-sm">
              {Object.entries(row.targetImages).map(([name, image]) => (
                <div key={name}>
                  <dt className="font-medium">Target image: {name}</dt>
                  <dd className="font-mono text-xs break-all">{image}</dd>
                </div>
              ))}
            </dl>
            <Conditions rows={row.conditions} />
          </article>
        ))}
      </div>
      <h3 className="font-semibold">Running and pending pods</h3>
      <p className="text-sm text-muted-foreground">
        Actual pod images and container image IDs below. Init containers may be
        successfully terminated; pod readiness is shown separately.
      </p>
      {snapshot.pods.length === 0 ? <p>No matching pods observed.</p> : null}
      {snapshot.pods.map((pod) => (
        <details key={pod.uid} className="min-w-0 border bg-card p-3">
          <summary className="cursor-pointer break-words">
            <span className="font-medium">{pod.name}</span> · {pod.phase} ·{" "}
            {pod.ready ? "Ready" : "Not ready"}
            {pod.terminating ? " · Terminating" : ""} · Node: {pod.nodeName}
          </summary>
          <div className="mt-3 space-y-3">
            <p className="text-sm break-all text-muted-foreground">
              {pod.role} · UID {pod.uid} · Created{" "}
              {formatTimestamp(pod.createdAt)}
            </p>
            <Conditions rows={pod.conditions} />
            {pod.containers.map((container) => (
              <div
                key={`${container.init}/${container.name}`}
                className="space-y-1 border-t pt-2 text-sm"
              >
                <p className="font-medium break-all">
                  {container.init ? "Init: " : ""}
                  {container.name}: {container.state}
                  {container.reason ? ` (${container.reason})` : ""}
                  {container.exitCode !== null
                    ? ` · Exit ${container.exitCode}`
                    : ""}{" "}
                  · Restarts {container.restarts} ·{" "}
                  {container.ready ? "Ready" : "Not ready"}
                </p>
                <p className="font-mono text-xs break-all">
                  Image: {container.image}
                </p>
                <p className="font-mono text-xs break-all">
                  Image ID: {container.imageId ?? "Not yet reported"}
                </p>
              </div>
            ))}
          </div>
        </details>
      ))}
      <h3 className="font-semibold">Migration jobs</h3>
      <p className="text-sm text-muted-foreground">
        Latest 20 matching jobs still retained by Kubernetes; completed jobs may
        expire.
      </p>
      {snapshot.jobs.length === 0 ? (
        <p>No retained migration jobs observed.</p>
      ) : null}
      {snapshot.jobs.map((job) => (
        <article
          key={job.name}
          className="min-w-0 space-y-2 border bg-card p-3"
        >
          <h4 className="font-medium break-all">
            {job.name}: {job.state}
          </h4>
          <p className="text-sm">
            Active {job.active} · Succeeded {job.succeeded} · Failed attempts{" "}
            {job.failed}
          </p>
          <p className="text-sm text-muted-foreground">
            Created {formatTimestamp(job.createdAt)}
            {job.finishedAt
              ? ` · Completed ${formatTimestamp(job.finishedAt)}`
              : ""}
          </p>
          {job.images.map((image) => (
            <p key={image} className="font-mono text-xs break-all">
              {image}
            </p>
          ))}
          <Conditions rows={job.conditions} />
        </article>
      ))}
    </div>
  )
}

function ReportedActivity({
  history,
  elapsed,
}: {
  history: DeploymentHistory
  elapsed: number
}) {
  return (
    <div className="space-y-3">
      <p className="text-sm text-muted-foreground">
        Latest 20 script-reported operations from shared database storage. Not
        an independent rollout verdict. History predating reporting, direct
        kubectl changes, and bootstrap activity may not be recorded. A Stage
        completion does not mean OnDelete workers or controllers were replaced.
      </p>
      {history.operations.length === 0 ? (
        <p>
          No recorded deployment activity. This does not mean no deployments
          occurred.
        </p>
      ) : null}
      {history.operations.map((operation) => {
        const unconfirmed =
          operation.status === "Running" &&
          Date.parse(history.observedAt) -
            Date.parse(operation.lastReportedAt) +
            elapsed >
            history.unconfirmedAfterSeconds * 1000
        return (
          <article
            key={operation.id}
            aria-label={`${operation.mode} ${operation.target}`}
            className="min-w-0 space-y-3 border bg-card p-4"
          >
            <h3 className="font-semibold break-all">
              {operation.mode}: {operation.target}
            </h3>
            <p
              className={
                unconfirmed || operation.status === "Failed"
                  ? "font-medium text-warning"
                  : "font-medium"
              }
            >
              {unconfirmed
                ? "Unconfirmed; last reported Running"
                : `Reported ${operation.status}`}
            </p>
            {unconfirmed ? (
              <p className="text-sm">
                No report for over {history.unconfirmedAfterSeconds} seconds.
                The reporter may have stopped; completion is unknown.
              </p>
            ) : null}
            <p className="text-sm break-all text-muted-foreground">
              {operation.actorKind} {operation.actor} · Operation {operation.id}
            </p>
            <p className="text-sm">
              Started {formatTimestamp(operation.startedAt)} · Last report{" "}
              {formatTimestamp(operation.lastReportedAt)}
            </p>
            {operation.events.length === 0 ? (
              <p>No steps reported yet.</p>
            ) : (
              <ol className="space-y-3 border-l-2 pl-4">
                {operation.events.map((event) => (
                  <li key={event.sequence} className="space-y-1 text-sm">
                    <p className="font-medium break-words">
                      {event.sequence}. {event.phase}
                      {event.subject ? ` · ${event.subject}` : ""} ·{" "}
                      {event.outcome}
                    </p>
                    <p className="text-muted-foreground">
                      <time dateTime={event.at}>
                        {formatTimestamp(event.at)}
                      </time>
                    </p>
                    <p className="break-words whitespace-pre-wrap">
                      {event.description}
                    </p>
                  </li>
                ))}
              </ol>
            )}
          </article>
        )
      })}
    </div>
  )
}

export function DeploymentsView() {
  const permissions = usePermissions({ scope: "machines", id: "*" })
  const permitted = permissions.can("Admin")
  const denied =
    (permissions.error instanceof ApiError &&
      permissions.error.status === 403) ||
    (permissions.isSuccess && !permissions.data?.admin)
  const [paused, setPaused] = useState(false)
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(timer)
  }, [])
  const actual = useQuery({
    queryKey: ["admin-deployments"],
    queryFn: ({ signal }) => getDeploymentSnapshot(signal),
    enabled: permitted && !paused,
    retry: false,
    refetchInterval: paused ? false : 5000,
    refetchIntervalInBackground: false,
  })
  const activity = useQuery({
    queryKey: ["admin-deployment-activity"],
    queryFn: ({ signal }) => getDeploymentHistory(signal),
    enabled: permitted && !paused,
    retry: false,
    refetchInterval: paused ? false : 5000,
    refetchIntervalInBackground: false,
  })
  const actualStale =
    actual.data !== undefined &&
    now - actual.dataUpdatedAt > actual.data.staleAfterSeconds * 1000
  const historyStale =
    activity.data !== undefined && now - activity.dataUpdatedAt > 20_000
  return (
    <section aria-label="Deployment transparency" className="min-w-0 space-y-5">
      <p className="text-sm text-muted-foreground">
        Read-only deployment visibility. This page does not deploy, stop, or
        roll back services. Actual rollout state and recorded release activity
        can differ.
      </p>
      <div className="flex flex-wrap items-center gap-3">
        <p role="status">
          {!permitted
            ? denied
              ? "Deployment access denied"
              : "Deployment permission checking"
            : paused
              ? "Paused snapshots; not live"
              : "Refresh every 5 seconds while visible"}
        </p>
        <Button
          variant="outline"
          disabled={!permitted}
          aria-pressed={paused}
          onClick={() => setPaused(!paused)}
        >
          {paused ? "Resume deployment updates" : "Pause deployment updates"}
        </Button>
        <Button
          variant="outline"
          disabled={!permitted || actual.isFetching || activity.isFetching}
          onClick={() => {
            void actual.refetch()
            void activity.refetch()
          }}
        >
          Refresh deployments
        </Button>
      </div>
      {permissions.error && !denied ? (
        <ErrorNotice error={permissions.error} />
      ) : null}
      {denied ? (
        <p role="alert">
          Global Admin permission is required to view deployments.
        </p>
      ) : null}
      {permitted ? (
        <>
          <section
            aria-label="Actual Kubernetes rollout"
            className="min-w-0 space-y-3"
          >
            <h2 className="text-lg font-semibold">Actual Kubernetes rollout</h2>
            {actual.isPending ? (
              <p role="status">Loading rollout observations...</p>
            ) : null}
            {actual.error ? <ErrorNotice error={actual.error} /> : null}
            {actualStale ? (
              <p role="alert">
                Rollout observation is stale; previous state hidden until
                refreshed.
              </p>
            ) : null}
            {actual.data && !actual.error && !actualStale ? (
              <ActualRollout snapshot={actual.data} />
            ) : null}
          </section>
          <section
            aria-label="Recorded deployment activity"
            className="min-w-0 space-y-3"
          >
            <h2 className="text-lg font-semibold">
              Recorded deployment activity
            </h2>
            {activity.isPending ? (
              <p role="status">Loading recorded activity...</p>
            ) : null}
            {activity.error ? <ErrorNotice error={activity.error} /> : null}
            {historyStale ? (
              <p role="alert">
                Activity history is stale; refresh to confirm the latest report.
              </p>
            ) : null}
            {activity.data && !activity.error && !historyStale ? (
              <ReportedActivity
                history={activity.data}
                elapsed={Math.max(0, now - activity.dataUpdatedAt)}
              />
            ) : null}
          </section>
        </>
      ) : null}
    </section>
  )
}
