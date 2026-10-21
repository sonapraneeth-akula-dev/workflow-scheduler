import { useQuery } from "@tanstack/react-query"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
import { formatTimestamp } from "@/lib/timestamps"
import { getKubernetesMemory } from "./api"

function Ram({
  bytes,
  sampledAt,
  observedAt,
  staleAfterSeconds,
}: {
  bytes: number | null
  sampledAt: string | null
  observedAt: number
  staleAfterSeconds: number
}) {
  if (bytes === null || sampledAt === null) return <>No RAM sample</>
  const age = observedAt - Date.parse(sampledAt)
  if (age > staleAfterSeconds * 1000 || age < -5000)
    return <span className="text-warning">Stale RAM sample</span>
  return (
    <>
      {(bytes / 1048576).toLocaleString(undefined, {
        maximumFractionDigits: 1,
      })}{" "}
      MiB
    </>
  )
}

export function KubernetesMemoryView({
  paused,
  now,
}: {
  paused: boolean
  now: number
}) {
  const query = useQuery({
    queryKey: ["admin-system-kubernetes-memory"],
    queryFn: ({ signal }) => getKubernetesMemory(signal),
    enabled: !paused,
    retry: false,
    refetchInterval: paused ? false : 15000,
    refetchIntervalInBackground: false,
  })
  const stale =
    query.data !== undefined &&
    !paused &&
    now - query.dataUpdatedAt > query.data.staleAfterSeconds * 1000
  const snapshot = !query.error && !stale ? query.data : undefined
  const observedAt = snapshot
    ? Date.parse(snapshot.observedAt) +
      (paused ? 0 : Math.max(0, now - query.dataUpdatedAt))
    : 0
  return (
    <section aria-label="Kubernetes RAM usage" className="min-w-0 space-y-3">
      <div className="flex flex-wrap items-center gap-3">
        <h2 className="font-semibold">K3s / Kubernetes RAM</h2>
        <Button
          variant="outline"
          disabled={query.isFetching}
          onClick={() => void query.refetch()}
        >
          Refresh RAM
        </Button>
      </div>
      <p className="text-sm text-muted-foreground">
        {paused
          ? "Paused RAM snapshot."
          : "RAM observations refresh every 15 seconds."}{" "}
        Metrics API working set, not reserved memory, disk storage or checkpoint
        persistence. Node usage includes K3s and other system processes; pod
        usage includes all its containers. Missing or stale measurements are not
        zero.
      </p>
      {query.isPending ? <p role="status">Loading Kubernetes RAM...</p> : null}
      {query.error ? <ErrorNotice error={query.error} /> : null}
      {stale ? (
        <p role="alert">
          No fresh Kubernetes RAM snapshot; old measurements hidden.
        </p>
      ) : null}
      {snapshot ? (
        <>
          <div
            role="region"
            aria-label="Node RAM inventory"
            tabIndex={0}
            className="max-w-full overflow-x-auto"
          >
            <table className="w-full text-left text-sm">
              <caption className="mb-2 text-left font-semibold">
                Node / VM RAM working set
              </caption>
              <thead>
                <tr>
                  {[
                    "Node",
                    "RAM used",
                    "Physical capacity",
                    "Allocatable to pods",
                    "RAM sample time",
                  ].map((title) => (
                    <th key={title} scope="col" className="p-2">
                      {title}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {snapshot.nodes.map((node) => (
                  <tr key={node.name} className="border-t align-top">
                    <th scope="row" className="p-2 font-normal">
                      {node.name}
                    </th>
                    <td className="p-2">
                      <Ram
                        bytes={node.workingSetBytes}
                        sampledAt={node.sampledAt}
                        observedAt={observedAt}
                        staleAfterSeconds={snapshot.staleAfterSeconds}
                      />
                    </td>
                    <td className="p-2">
                      {(node.capacityBytes / 1048576).toLocaleString(
                        undefined,
                        { maximumFractionDigits: 1 }
                      )}{" "}
                      MiB
                    </td>
                    <td className="p-2">
                      {(node.allocatableBytes / 1048576).toLocaleString(
                        undefined,
                        { maximumFractionDigits: 1 }
                      )}{" "}
                      MiB
                    </td>
                    <td className="p-2">
                      {node.sampledAt ? (
                        <time dateTime={node.sampledAt}>
                          {formatTimestamp(node.sampledAt)}
                        </time>
                      ) : (
                        "Not reported"
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            {!snapshot.nodes.length ? (
              <p>No node observations returned.</p>
            ) : null}
          </div>
          <div
            role="region"
            aria-label="Pod RAM inventory"
            tabIndex={0}
            className="max-w-full overflow-x-auto"
          >
            <table className="w-full text-left text-sm">
              <caption className="mb-2 text-left font-semibold">
                Application pod RAM working set
              </caption>
              <thead>
                <tr>
                  {[
                    "Pod",
                    "Role",
                    "Host node",
                    "Pod state",
                    "RAM used",
                    "RAM sample time",
                  ].map((title) => (
                    <th key={title} scope="col" className="p-2">
                      {title}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {snapshot.pods.map((pod) => (
                  <tr key={pod.uid} className="border-t align-top">
                    <th scope="row" className="p-2 font-normal">
                      <p>{pod.name}</p>
                      <p className="text-xs">{pod.namespace}</p>
                    </th>
                    <td className="p-2">{pod.role}</td>
                    <td className="p-2">{pod.nodeName}</td>
                    <td className="p-2">
                      {pod.phase} / {pod.ready ? "Ready" : "Not ready"}
                    </td>
                    <td className="p-2">
                      <Ram
                        bytes={pod.workingSetBytes}
                        sampledAt={pod.sampledAt}
                        observedAt={observedAt}
                        staleAfterSeconds={snapshot.staleAfterSeconds}
                      />
                    </td>
                    <td className="p-2">
                      {pod.sampledAt ? (
                        <time dateTime={pod.sampledAt}>
                          {formatTimestamp(pod.sampledAt)}
                        </time>
                      ) : (
                        "Not reported"
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            {!snapshot.pods.length ? (
              <p>No matching application pods returned.</p>
            ) : null}
          </div>
        </>
      ) : null}
    </section>
  )
}
