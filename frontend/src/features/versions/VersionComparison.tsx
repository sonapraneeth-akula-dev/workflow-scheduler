import { useMemo } from "react"
import { useQuery } from "@tanstack/react-query"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
import { ReportRenderer } from "@/renderers/ReportRenderer"
import { getVersionDiff, type VersionDiff } from "./api"

function ManifestChanges({ manifest }: { manifest: VersionDiff["manifest"] }) {
  return (
    <section aria-label="Manifest changes" className="min-w-0 space-y-4">
      <h3 className="text-lg font-semibold">Manifest changes</h3>
      {Object.entries(manifest).map(([category, entries]) => (
        <section
          key={category}
          aria-label={`${category} changes`}
          className="min-w-0 space-y-2"
        >
          <h4 className="font-semibold capitalize">{category}</h4>
          {entries.length === 0 ? <p>No {category} changes.</p> : null}
          {entries.map((entry) => (
            <details key={entry.key} className="min-w-0 rounded border p-3">
              <summary className="cursor-pointer break-words">
                {entry.kind}: {entry.key}
              </summary>
              <div className="grid min-w-0 gap-3 pt-3 md:grid-cols-2">
                {(["before", "after"] as const).map((side) => (
                  <section
                    key={side}
                    aria-label={`${category} ${entry.key} ${side}`}
                    className="min-w-0"
                  >
                    <h5 className="font-medium capitalize">{side}</h5>
                    <pre className="max-h-96 overflow-auto text-sm break-words whitespace-pre-wrap">
                      {entry[side] === null
                        ? "Not present"
                        : JSON.stringify(entry[side], null, 2)}
                    </pre>
                  </section>
                ))}
              </div>
            </details>
          ))}
        </section>
      ))}
    </section>
  )
}

export function VersionComparison({
  workflowId,
  from,
  to,
}: {
  workflowId: string
  from: string
  to: string
}) {
  const query = useQuery({
    queryKey: ["version-diff", workflowId, from, to],
    queryFn: ({ signal }) => getVersionDiff(workflowId, from, to, signal),
  })
  const diff = query.error ? undefined : query.data
  const content = useMemo(
    () =>
      diff
        ? new Blob(
            [
              JSON.stringify({
                original: diff.source.before,
                modified: diff.source.after,
              }),
            ],
            { type: "application/json" }
          )
        : null,
    [diff]
  )
  return (
    <section aria-label="Version comparison" className="min-w-0 space-y-4">
      <h2 className="text-xl font-semibold">Version comparison</h2>
      <p className="break-all">
        From {from} to {to}.
      </p>
      <Button
        variant="outline"
        disabled={query.isFetching}
        onClick={() => void query.refetch()}
      >
        Refresh comparison
      </Button>
      {query.isPending ? (
        <p role="status">Loading immutable version comparison...</p>
      ) : null}
      {query.error ? <ErrorNotice error={query.error} /> : null}
      {diff && content ? (
        <>
          <p>
            Version {diff.fromVersion} compared with {diff.toVersion}.
          </p>
          <ManifestChanges manifest={diff.manifest} />
          <h3 className="text-lg font-semibold">Script source</h3>
          <p>
            Retained authored source and literal configuration. Secret
            references are never resolved.{" "}
            {diff.source.changed ? "Source changed." : "No source changes."}
          </p>
          <ReportRenderer
            kind="diff"
            title="Workflow source"
            content={content}
          />
        </>
      ) : null}
    </section>
  )
}
