import { Link } from "@tanstack/react-router"
import { StatusBadge } from "@/components/StatusBadge"
import type { RunSnapshot } from "@/lib/live/runApi"

export function RunLineage({ run }: { run: RunSnapshot }) {
  if (!run.parentRunId) return null
  return (
    <section aria-label="Run lineage" className="min-w-0 space-y-3 break-words">
      <h2 className="text-xl font-semibold">Run lineage</h2>
      <p className="break-all">
        Parent:{" "}
        <Link to="/runs/$id" params={{ id: run.parentRunId }}>
          {run.parentRunId}
        </Link>
      </p>
      {Object.keys(run.reusedNodes).length ? (
        <>
          <p>
            Reused nodes did not execute new attempts. Follow their source runs
            for original outputs and attempts; access is checked there.
          </p>
          <ul aria-label="Reused nodes" className="space-y-2">
            {Object.entries(run.reusedNodes)
              .sort(([left], [right]) => left.localeCompare(right))
              .map(([node, source]) => (
                <li key={node} className="break-all">
                  {node} <StatusBadge status="Reused" />{" "}
                  <Link
                    to="/runs/$id"
                    params={{ id: source }}
                    aria-label={`${node}: original outputs in run ${source}`}
                  >
                    Original outputs: {source}
                  </Link>
                </li>
              ))}
          </ul>
        </>
      ) : (
        <p>No nodes reused from the source run.</p>
      )}
    </section>
  )
}
