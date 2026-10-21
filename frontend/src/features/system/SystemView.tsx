import { useEffect, useState, type ReactNode } from "react"
import { useQuery, useQueryClient } from "@tanstack/react-query"
import { Link } from "@tanstack/react-router"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
import { usePermissions } from "@/lib/auth/usePermissions"
import { ApiError } from "@/lib/api/client"
import { formatTimestamp } from "@/lib/timestamps"
import { getSystem, systemFrame, type SystemFrame } from "./api"
import "./SystemView.css"

const queryKey = ["admin-system"] as const
const cardWidth = 208
const cardHeight = 156
const rowHeight = 184
const columns = [16, 280, 544, 808, 1072] as const
const center = (row: number) => 40 + row * rowHeight + cardHeight / 2
type Instance = SystemFrame["snapshot"]["instances"][number]
const currentProcess = (row: Instance) =>
  row.status === "Healthy" || row.status === "Draining"

function controllerRole(row: Instance) {
  if (row.role !== "ApiController") return "Not a controller"
  if (row.status !== "Healthy") return "No active lease"
  return row.isLeader ? "Active leader" : "Passive standby"
}

function Health({ status }: { status: string }) {
  return (
    <span
      className={`font-semibold ${
        status === "Healthy"
          ? "text-success"
          : status === "Unhealthy"
            ? "text-destructive"
            : "text-warning"
      }`}
    >
      {status}
    </span>
  )
}

function Card({
  column,
  row,
  title,
  children,
}: {
  column: number
  row: number
  title: string
  children: ReactNode
}) {
  return (
    <div
      className="absolute flex flex-col gap-1 border bg-card p-3 text-xs text-card-foreground"
      style={{
        left: column,
        top: 40 + row * rowHeight,
        width: cardWidth,
        minHeight: cardHeight,
      }}
    >
      <p className="line-clamp-2 text-sm font-semibold break-all" title={title}>
        {title}
      </p>
      {children}
    </div>
  )
}

function Wire({
  from,
  to,
  pulse,
  reverse = false,
}: {
  from: [number, number]
  to: [number, number]
  pulse?: string
  reverse?: boolean
}) {
  const start = reverse ? to : from
  const end = reverse ? from : to
  const middle = (start[0] + end[0]) / 2
  const path = `M${start[0]},${start[1]} C${middle},${start[1]} ${middle},${end[1]} ${end[0]},${end[1]}`
  return (
    <g>
      <path d={path} fill="none" stroke="var(--border)" strokeWidth="2" />
      {pulse ? (
        <path
          key={pulse}
          d={path}
          fill="none"
          stroke="var(--primary)"
          strokeWidth="3"
          className="kardia-system-flow"
        />
      ) : null}
    </g>
  )
}

function Topology({
  frame,
  animate,
}: {
  frame: SystemFrame
  animate: boolean
}) {
  const { snapshot, respondingInstanceId } = frame
  const instances = snapshot.instances.filter(currentProcess)
  const workers = snapshot.workers
  const controllers = instances.filter((row) => row.role === "ApiController")
  const leaderIndex = controllers.findIndex((row) => row.isLeader)
  const leader = controllers[leaderIndex]
  const response = animate ? snapshot.observedAt : undefined
  const height = 64 + Math.max(1, instances.length, workers.length) * rowHeight
  return (
    <figure className="space-y-2">
      <figcaption id="system-topology-caption" className="font-semibold">
        Observed application flow
      </figcaption>
      <p className="text-sm text-muted-foreground">
        Pulses: snapshot requests and responses, increased assignments, worker
        heartbeats, or live sends. Lines show logical paths through durable
        state, not packet traces. All current controller replicas appear,
        including passive standbys; these are the same processes listed in the
        API tier. Inactive process observations appear separately below.
      </p>
      <div
        role="region"
        aria-labelledby="system-topology-caption"
        tabIndex={0}
        className="max-h-[42rem] max-w-full overflow-auto border bg-muted/30"
      >
        <div className="relative" style={{ width: 1300, height }}>
          <svg
            aria-hidden="true"
            className="absolute inset-0 h-full w-full"
            viewBox={`0 0 1300 ${height}`}
          >
            {instances.map((row, index) => (
              <g key={row.id}>
                <Wire
                  from={[columns[0] + cardWidth, center(0) - 4]}
                  to={[columns[1], center(index) - 4]}
                  pulse={row.id === respondingInstanceId ? response : undefined}
                />
                <Wire
                  from={[columns[0] + cardWidth, center(0) + 4]}
                  to={[columns[1], center(index) + 4]}
                  pulse={row.id === respondingInstanceId ? response : undefined}
                  reverse
                />
                <Wire
                  from={[columns[1] + cardWidth, center(index)]}
                  to={[columns[2], center(0)]}
                  pulse={row.id === respondingInstanceId ? response : undefined}
                />
              </g>
            ))}
            {controllers.map((controller, index) => (
              <Wire
                key={controller.id}
                from={[columns[2] + cardWidth, center(0)]}
                to={[columns[3], center(index)]}
              />
            ))}
            {leader
              ? workers.map((worker, index) => (
                  <g key={worker.id}>
                    <Wire
                      from={[columns[3] + cardWidth, center(leaderIndex) - 4]}
                      to={[columns[4], center(index) - 4]}
                      pulse={
                        animate && frame.dispatchWorkers.includes(worker.id)
                          ? snapshot.observedAt
                          : undefined
                      }
                    />
                    <Wire
                      from={[columns[3] + cardWidth, center(leaderIndex) + 4]}
                      to={[columns[4], center(index) + 4]}
                      pulse={
                        animate && frame.heartbeatWorkers.includes(worker.id)
                          ? worker.lastHeartbeatAt
                          : undefined
                      }
                      reverse
                    />
                  </g>
                ))
              : null}
          </svg>
          {[
            "Browser / frontend",
            "API processes",
            "Durable state",
            "Controllers: active / passive",
            "Worker machines / pods",
          ].map((title, index) => (
            <p
              key={title}
              className="absolute top-3 text-sm font-semibold"
              style={{ left: columns[index], width: cardWidth }}
            >
              {title}
            </p>
          ))}
          <Card column={columns[0]} row={0} title="This browser">
            <p>Kardia frontend</p>
            <p>Authenticated snapshot request / response</p>
            <p className="text-xs text-muted-foreground">
              Frontend server placement is not reported.
            </p>
          </Card>
          {instances.map((row, index) => (
            <Card key={row.id} column={columns[1]} row={index} title={row.name}>
              <p>
                <Health status={row.status} />
                {row.isLeader ? " / leader" : ""}
              </p>
              <p className="truncate" title={row.nodeName}>
                Host node: {row.nodeName}
              </p>
              <p>
                {row.role === "ApiController" ? "API + controller" : "API only"}
              </p>
              <p>
                {row.liveConnections} live connections
                {frame.messageInstances.includes(row.id)
                  ? "; sends observed"
                  : ""}
              </p>
            </Card>
          ))}
          {!instances.length ? (
            <Card column={columns[1]} row={0} title="No process heartbeats">
              <p>No observed API/controller instances in this snapshot.</p>
            </Card>
          ) : null}
          <Card column={columns[2]} row={0} title="PostgreSQL">
            <p>Durable runs, assignments and events</p>
            <p>Snapshot read succeeded</p>
            <p className="text-xs text-muted-foreground">
              Database placement is not reported.
            </p>
          </Card>
          {controllers.map((controller, index) => (
            <Card
              key={controller.id}
              column={columns[3]}
              row={index}
              title={controller.name}
            >
              <p>
                <Health status={controller.status} />
              </p>
              <p>{controllerRole(controller)}</p>
              <p className="truncate" title={controller.nodeName}>
                Host node: {controller.nodeName}
              </p>
              <p className="text-xs text-muted-foreground">
                Same process as the API entry; not another replica.
              </p>
            </Card>
          ))}
          {!controllers.length ? (
            <Card
              column={columns[3]}
              row={0}
              title="No active controller lease"
            >
              <p>No fresh ready controller process is observed.</p>
            </Card>
          ) : null}
          {workers.map((worker, index) => (
            <Card
              key={worker.id}
              column={columns[4]}
              row={index}
              title={worker.name}
            >
              <p>
                <Health status={worker.status} />
              </p>
              <p>
                {worker.runningTasks} running / {worker.assignedTasks} assigned
                tasks
              </p>
              <p>
                {worker.activeDagNodes} DAG nodes / {worker.activeRuns} runs
              </p>
              <Link
                to="/machines/$id"
                params={{ id: worker.id }}
                className="underline"
              >
                Machine details
              </Link>
            </Card>
          ))}
          {!workers.length ? (
            <Card column={columns[4]} row={0} title="No registered workers">
              <p>No worker has registered with this database.</p>
            </Card>
          ) : null}
        </div>
      </div>
      {controllers.length > 0 && !leader ? (
        <p role="alert">
          No active controller lease; standby processes do not imply a healthy
          leader.
        </p>
      ) : null}
    </figure>
  )
}

function ProcessInventory({
  instances,
  historical = false,
}: {
  instances: Instance[]
  historical?: boolean
}) {
  return (
    <>
      <div
        role="region"
        aria-label={
          historical
            ? "Inactive application instance inventory"
            : "Application instance inventory"
        }
        tabIndex={0}
        className="max-w-full overflow-x-auto"
      >
        <table className="w-full text-left text-sm text-foreground">
          <caption className="mb-2 text-left font-semibold">
            {historical
              ? "Stopped and missed-heartbeat process observations"
              : "API and controller processes"}
          </caption>
          <thead>
            <tr>
              {[
                "Instance",
                "Role / lease",
                "Host node / pod",
                "Health",
                "Requests / live sends",
                "Live clients / subscriptions",
                "Last heartbeat",
              ].map((title) => (
                <th key={title} scope="col" className="p-2">
                  {title}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {instances.map((row) => (
              <tr key={row.id} className="border-t align-top">
                <th
                  scope="row"
                  className="max-w-64 p-2 text-left font-normal break-all"
                >
                  <p className="font-semibold">{row.name}</p>
                  <p className="text-xs">{row.id}</p>
                  <p>Version {row.version}</p>
                </th>
                <td className="p-2">
                  {row.role === "ApiController"
                    ? "API + controller"
                    : "API only"}
                  <p>{controllerRole(row)}</p>
                </td>
                <td className="max-w-64 p-2 break-all">
                  <p>Node: {row.nodeName}</p>
                  <p>Host: {row.hostName}</p>
                  <p>Pod: {row.podName ?? "Not reported"}</p>
                  <p>Namespace: {row.namespace ?? "Not reported"}</p>
                </td>
                <td className="p-2">
                  <Health status={row.status} />
                </td>
                <td className="p-2">
                  {row.httpRequests} / {row.liveDeliveries}
                </td>
                <td className="p-2">
                  {row.liveConnections} / {row.liveSubscriptions}
                </td>
                <td className="p-2">
                  <time dateTime={row.lastHeartbeatAt}>
                    {formatTimestamp(row.lastHeartbeatAt)}
                  </time>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {!instances.length ? <p>No observed application processes.</p> : null}
      </div>
    </>
  )
}

function Inventories({ frame }: { frame: SystemFrame }) {
  const instances = frame.snapshot.instances.filter(currentProcess)
  const inactive = frame.snapshot.instances.filter(
    (row) => !currentProcess(row)
  )
  return (
    <>
      <ProcessInventory instances={instances} />
      {inactive.length ? (
        <details
          aria-label={`Inactive process observations (${inactive.length})`}
        >
          <summary className="cursor-pointer font-semibold">
            Inactive process observations ({inactive.length})
          </summary>
          <p className="my-2 text-sm text-muted-foreground">
            Stopped means that process reported shutdown. Unhealthy means its
            heartbeat expired, not proof that it stopped. Rollouts retain old
            process observations for up to 24 hours; these are not current
            replica counts.
          </p>
          <ProcessInventory instances={inactive} historical />
        </details>
      ) : null}
      <div
        role="region"
        aria-label="Worker assignment inventory"
        tabIndex={0}
        className="max-w-full overflow-x-auto"
      >
        <table className="w-full text-left text-sm text-foreground">
          <caption className="mb-2 text-left font-semibold">
            Workers and assigned work
          </caption>
          <thead>
            <tr>
              {[
                "Worker",
                "Pool",
                "Health",
                "Running / assigned tasks",
                "Active DAG nodes",
                "Active runs",
                "Assigned workflows",
                "Slots used / maximum",
                "Last heartbeat",
              ].map((title) => (
                <th key={title} scope="col" className="p-2">
                  {title}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {frame.snapshot.workers.map((row) => (
              <tr key={row.id} className="border-t align-top">
                <th
                  scope="row"
                  className="max-w-64 p-2 text-left font-normal break-all"
                >
                  <Link
                    to="/machines/$id"
                    params={{ id: row.id }}
                    className="underline"
                  >
                    {row.name}
                  </Link>
                </th>
                <td className="max-w-48 p-2 break-all">{row.pool}</td>
                <td className="p-2">
                  <Health status={row.status} />
                </td>
                <td className="p-2">
                  {row.runningTasks} / {row.assignedTasks}
                </td>
                <td className="p-2">{row.activeDagNodes}</td>
                <td className="p-2">{row.activeRuns}</td>
                <td className="p-2">{row.activeWorkflows}</td>
                <td className="p-2">
                  {row.slotsUsed} / {row.maxTasks}
                </td>
                <td className="p-2">
                  <time dateTime={row.lastHeartbeatAt}>
                    {formatTimestamp(row.lastHeartbeatAt)}
                  </time>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {!frame.snapshot.workers.length ? <p>No registered workers.</p> : null}
      </div>
    </>
  )
}

export function SystemView() {
  const permissions = usePermissions({ scope: "machines", id: "*" })
  const permitted = permissions.can("Admin")
  const denied =
    (permissions.error instanceof ApiError &&
      permissions.error.status === 403) ||
    (permissions.isSuccess && !permissions.data?.admin)
  const [paused, setPaused] = useState(false)
  const [motion, setMotion] = useState(true)
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(timer)
  }, [])
  const client = useQueryClient()
  const query = useQuery({
    queryKey,
    queryFn: async ({ signal }) =>
      systemFrame(
        await getSystem(signal),
        client.getQueryData<SystemFrame>(queryKey)
      ),
    enabled: permitted && !paused,
    retry: false,
    refetchInterval: paused ? false : 2000,
    refetchIntervalInBackground: false,
  })
  const stale =
    query.data !== undefined &&
    now - query.dataUpdatedAt >
      query.data.snapshot.heartbeatTimeoutSeconds * 1000
  const frame =
    permitted && !query.error && (!stale || paused) ? query.data : undefined
  const totals = frame?.snapshot.totals
  const currentInstances =
    frame?.snapshot.instances.filter(currentProcess) ?? []
  const apiReplicas = currentInstances.filter((row) => row.role === "Api")
  const controllers = currentInstances.filter(
    (row) => row.role === "ApiController" && row.status === "Healthy"
  )
  return (
    <section aria-label="Live system inventory" className="min-w-0 space-y-5">
      <div className="flex flex-wrap items-center gap-3">
        <p role="status">
          {!permitted
            ? denied
              ? "System access denied"
              : "System permission checking"
            : query.error || (stale && !paused)
              ? "System updates unavailable"
              : paused
                ? "Paused snapshot; not live"
                : "Live snapshots every 2 seconds"}
        </p>
        <Button
          variant="outline"
          disabled={!permitted}
          aria-pressed={paused}
          onClick={() => setPaused(!paused)}
        >
          {paused ? "Resume live updates" : "Pause live updates"}
        </Button>
        <Button
          variant="outline"
          disabled={!permitted || query.isFetching}
          onClick={() => void query.refetch()}
        >
          Refresh system
        </Button>
        <label className="flex items-center gap-2">
          <input
            type="checkbox"
            checked={motion}
            onChange={(event) => setMotion(event.target.checked)}
          />
          Animate observed activity
        </label>
      </div>
      {permissions.error && !denied ? (
        <ErrorNotice error={permissions.error} />
      ) : null}
      {denied ? (
        <p role="alert">
          Global Admin permission is required to view the system.
        </p>
      ) : null}
      {permitted && query.isPending ? (
        <p role="status">Loading system inventory...</p>
      ) : null}
      {query.error ? <ErrorNotice error={query.error} /> : null}
      {permitted && stale && !paused ? (
        <p role="alert">
          No fresh system snapshot; last inventory hidden until updates recover.
        </p>
      ) : null}
      {frame && totals ? (
        <>
          <p className="text-sm">
            Database observation:{" "}
            <time dateTime={frame.snapshot.observedAt}>
              {formatTimestamp(frame.snapshot.observedAt)}
            </time>
            . Heartbeats expire after {frame.snapshot.heartbeatTimeoutSeconds}{" "}
            seconds. Application observations, not independent host probes.
          </p>
          <dl className="grid grid-cols-2 gap-3 md:grid-cols-4">
            {Object.entries({
              "API-only replicas (healthy / current)": `${apiReplicas.filter((row) => row.status === "Healthy").length} / ${apiReplicas.length}`,
              "Controllers (active / passive)": `${controllers.filter((row) => row.isLeader).length} / ${controllers.filter((row) => !row.isLeader).length}`,
              "Draining processes": currentInstances.filter(
                (row) => row.status === "Draining"
              ).length,
              "Inactive process observations":
                frame.snapshot.instances.length - currentInstances.length,
              "Workers (healthy / registered)": `${frame.snapshot.workers.filter((row) => row.status === "Healthy").length} / ${frame.snapshot.workers.length}`,
              "Active workflows": totals.activeWorkflows,
              "Queued / running / aborting runs": `${totals.queuedRuns} / ${totals.runningRuns} / ${totals.abortingRuns}`,
              "Running tasks": totals.runningTasks,
              "Assigned tasks": totals.assignedTasks,
              "Active DAG nodes": totals.activeDagNodes,
            }).map(([label, value]) => (
              <div
                key={label}
                className="border bg-card p-3 text-card-foreground"
              >
                <dt className="text-sm text-muted-foreground">{label}</dt>
                <dd className="text-2xl font-semibold">{value}</dd>
              </div>
            ))}
          </dl>
          <Topology frame={frame} animate={motion && !paused} />
          <p className="text-sm text-muted-foreground">
            Assigned work can remain on an unhealthy worker until recovery and
            physical fencing settle. Per-worker run and workflow counts overlap
            across workers. Counters are cumulative per process.
          </p>
          <Inventories frame={frame} />
        </>
      ) : null}
    </section>
  )
}
