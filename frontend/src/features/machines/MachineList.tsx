import { useEffect, useState, type FormEvent } from "react"
import { useQuery, useQueryClient } from "@tanstack/react-query"
import { Link } from "@tanstack/react-router"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
import { usePermissions } from "@/lib/auth/usePermissions"
import { useLive } from "@/lib/live/useLive"
import { EditLabelsDialog } from "./EditLabelsDialog"
import {
  formatLabels,
  getMachines,
  machineSearchSchema,
  type Machine,
  type MachineSearch,
} from "./api"

export function MachineFilters({
  search,
  onChange,
}: {
  search: MachineSearch
  onChange: (next: MachineSearch) => void
}) {
  const [error, setError] = useState<Error>()
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const form = new FormData(event.currentTarget)
    const pool = String(form.get("pool") ?? "")
    const os = String(form.get("os") ?? "")
    const status = String(form.get("status") ?? "")
    const labels = String(form.get("label") ?? "")
    const result = machineSearchSchema.safeParse({
      ...(pool ? { pool } : {}),
      ...(os ? { os } : {}),
      ...(status ? { status } : {}),
      ...(labels ? { label: labels.split(/\r?\n/) } : {}),
    })
    if (!result.success) {
      setError(result.error)
      return
    }
    setError(undefined)
    onChange(result.data)
  }
  return (
    <form
      aria-label="Machine filters"
      onSubmit={submit}
      className="flex min-w-0 flex-wrap items-end gap-3"
    >
      <label className="flex min-w-0 flex-col gap-1">
        Pool (exact)
        <input
          name="pool"
          defaultValue={search.pool ?? ""}
          maxLength={200}
          className="min-w-0 rounded border bg-background p-2"
        />
      </label>
      <label className="flex min-w-0 flex-col gap-1">
        OS
        <select
          aria-label="OS"
          name="os"
          defaultValue={search.os ?? ""}
          className="rounded border bg-background p-2"
        >
          <option value="">All operating systems</option>
          <option value="windows">Windows</option>
          <option value="linux">Linux</option>
        </select>
      </label>
      <label className="flex min-w-0 flex-col gap-1">
        Health
        <select
          aria-label="Health"
          name="status"
          defaultValue={search.status ?? ""}
          className="rounded border bg-background p-2"
        >
          <option value="">All health states</option>
          {["Healthy", "Unhealthy", "Draining"].map((status) => (
            <option key={status}>{status}</option>
          ))}
        </select>
      </label>
      <label className="flex min-w-0 flex-col gap-1">
        Label filters (one key=value per line)
        <textarea
          name="label"
          defaultValue={search.label?.join("\n") ?? ""}
          rows={2}
          className="min-w-0 rounded border bg-background p-2"
        />
      </label>
      <Button type="submit">Filter machines</Button>
      <Button type="button" variant="outline" onClick={() => onChange({})}>
        Clear machine filters
      </Button>
      {error ? <ErrorNotice error={error} /> : null}
    </form>
  )
}

function Reservation({ machine }: { machine: Machine }) {
  const reservation = machine.currentReservation
  return reservation ? (
    <div className="space-y-1 break-all">
      <Link
        to="/runs/$id"
        params={{ id: reservation.runId }}
        className="underline"
      >
        Run {reservation.runId}
      </Link>
      <p>Node: {reservation.nodeKey}</p>
      <p>
        Reservation: {reservation.id}; epoch {reservation.epoch}
      </p>
      <p>
        Lease expiry:{" "}
        <time dateTime={reservation.expiresAt}>{reservation.expiresAt}</time>
      </p>
      <p>
        {reservation.releasePending
          ? "Release pending physical acknowledgement"
          : "Reserved"}
      </p>
      <p>Lease expiry alone does not release this machine.</p>
    </div>
  ) : (
    <span>Not reserved</span>
  )
}

export function MachineList({
  search = {},
  id,
}: {
  search?: MachineSearch
  id?: string
}) {
  const permissions = usePermissions({ scope: "machines", id: "*" })
  // Keep drafts mounted during revalidation; mutation permission still fails closed.
  const permitted = permissions.isSuccess && permissions.data?.viewer === true
  const { snapshot, retry } = useLive(
    permitted ? { scope: "machines", id: "*" } : undefined
  )
  const client = useQueryClient()
  const query = useQuery({
    queryKey: ["machines", search],
    queryFn: ({ signal }) => getMachines(search, signal),
    enabled: permitted,
    retry: false,
  })
  const live = snapshot?.status === "live"
  const revision = snapshot?.resyncVersion
  useEffect(() => {
    if (live) void client.invalidateQueries({ queryKey: ["machines"] })
  }, [client, live, revision])
  const rows =
    permitted && !query.error && snapshot?.status !== "error"
      ? query.data
      : undefined
  const machine = rows?.find((row) => row.id === id)
  return (
    <section
      aria-label={id ? "Machine detail" : "Machine inventory"}
      className="min-w-0 space-y-4"
    >
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p role="status">
          Fleet updates: {snapshot?.status ?? "permission checking"}
          {query.isFetching ? "; refreshing inventory" : ""}
        </p>
        <Button
          variant="outline"
          disabled={!permitted || query.isFetching}
          onClick={() => {
            retry()
            void query.refetch()
          }}
        >
          Refresh machines
        </Button>
      </div>
      {permissions.error ? <ErrorNotice error={permissions.error} /> : null}
      {permissions.isSuccess && !permissions.data.viewer ? (
        <p role="alert">
          Global Admin permission is required to view machines.
        </p>
      ) : null}
      {permitted && query.isPending ? (
        <p role="status">Loading machines...</p>
      ) : null}
      {query.error ? <ErrorNotice error={query.error} /> : null}
      {snapshot?.error ? <ErrorNotice error={snapshot.error} /> : null}
      {snapshot && !live && !snapshot.error ? (
        <p>Live updates unavailable; displayed data may be stale.</p>
      ) : null}
      {id ? (
        machine ? (
          <div className="min-w-0 space-y-4">
            <h2 className="text-xl font-semibold break-words">
              {machine.name}
            </h2>
            <EditLabelsDialog
              machine={machine}
              canEdit={permissions.can("Admin")}
            />
            <dl className="grid min-w-0 gap-3 sm:grid-cols-2">
              {Object.entries({
                "Machine ID": machine.id,
                OS: `${machine.os} / ${machine.arch}`,
                Health: machine.status,
                Pool: machine.pool,
                Labels: formatLabels(machine.labels) || "No labels",
                Capacity: `${machine.cores} cores; ${machine.ramMb} MiB RAM; ${machine.slotsUsed} / ${machine.maxTasks} task slots used`,
                "CPU usage": `${machine.cpuPercent}%`,
                "RAM usage": `${machine.ramUsedMb} MiB`,
                "Free disk": `${machine.diskFreeMb} MiB`,
                "Agent version": machine.version,
                "Last heartbeat": machine.lastHeartbeatAt,
              }).map(([name, value]) => (
                <div key={name} className="min-w-0">
                  <dt className="font-semibold">{name}</dt>
                  <dd className="break-words whitespace-pre-wrap">{value}</dd>
                </div>
              ))}
            </dl>
            <h3 className="font-semibold">Current reservation</h3>
            <Reservation machine={machine} />
          </div>
        ) : rows ? (
          <p role="alert">Machine not found in the current inventory.</p>
        ) : null
      ) : rows?.length ? (
        <div
          role="region"
          aria-label="Machine table"
          tabIndex={0}
          className="max-w-full overflow-x-auto"
        >
          <table className="w-full text-left text-sm">
            <caption className="sr-only">
              Registered machines and current capacity
            </caption>
            <thead>
              <tr>
                {[
                  "Machine",
                  "OS",
                  "Labels",
                  "Pool",
                  "Health",
                  "Capacity",
                  "Current reservation",
                ].map((title) => (
                  <th key={title} scope="col" className="p-2">
                    {title}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <tr key={row.id} className="border-t align-top">
                  <td className="max-w-56 p-2 break-words">
                    <Link
                      to="/machines/$id"
                      params={{ id: row.id }}
                      className="underline"
                    >
                      {row.name}
                    </Link>
                  </td>
                  <td className="p-2">
                    {row.os} / {row.arch}
                  </td>
                  <td className="max-w-56 p-2 break-words whitespace-pre-wrap">
                    {formatLabels(row.labels) || "No labels"}
                  </td>
                  <td className="max-w-48 p-2 break-words">{row.pool}</td>
                  <td className="p-2">{row.status}</td>
                  <td className="p-2">
                    {row.cores} cores; {row.ramMb} MiB RAM; {row.slotsUsed} /{" "}
                    {row.maxTasks} task slots used
                  </td>
                  <td className="max-w-64 min-w-48 p-2">
                    <Reservation machine={row} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : rows ? (
        <p>No matching machines.</p>
      ) : null}
    </section>
  )
}
