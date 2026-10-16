import { useState, type FormEvent } from "react"
import { useInfiniteQuery } from "@tanstack/react-query"
import { Link } from "@tanstack/react-router"
import { ErrorNotice } from "@/components/ErrorNotice"
import { Button } from "@/components/ui/button"
import { getVersions } from "./api"
import { VersionComparison } from "./VersionComparison"
import { CloneDialog } from "./CloneDialog"

export function VersionsTab({
  workflowId,
  pinnedVersionId,
}: {
  workflowId: string
  pinnedVersionId?: string
}) {
  const [team = "", type = "", wf = ""] = workflowId.split("/")
  const [from, setFrom] = useState<string>()
  const [to, setTo] = useState<string>()
  const [comparison, setComparison] = useState<{ from: string; to: string }>()
  const query = useInfiniteQuery({
    queryKey: ["workflow-versions", workflowId],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam, signal }) =>
      getVersions(workflowId, pageParam, signal),
    getNextPageParam: (page) => page.nextCursor ?? undefined,
  })
  const pages = query.error ? undefined : query.data?.pages
  const versions = pages?.flatMap((page) => page.items)
  const duplicate =
    versions &&
    new Set(versions.map((version) => version.versionId)).size !==
      versions.length
  const latestPage = pages?.at(-1)
  const selectedFrom =
    from ??
    pinnedVersionId ??
    versions?.[1]?.versionId ??
    versions?.[0]?.versionId ??
    ""
  const selectedTo = to ?? versions?.[0]?.versionId ?? ""
  function compare(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setComparison({ from: selectedFrom, to: selectedTo })
  }
  const Heading = pinnedVersionId ? "h2" : "h1"
  return (
    <section aria-label="Workflow versions" className="min-w-0 space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <Heading className="text-2xl font-semibold">Versions</Heading>
        <Button
          variant="outline"
          disabled={query.isFetching}
          onClick={() => void query.refetch()}
        >
          Refresh versions
        </Button>
      </div>
      <p className="break-words">{workflowId}</p>
      {pinnedVersionId ? (
        <CloneDialog workflowId={workflowId} versionId={pinnedVersionId} />
      ) : null}
      <p>
        Newest registration first. Times shown in UTC; active and latest may
        differ after rollback. History and comparison are read-only.
      </p>
      {query.isPending ? <p role="status">Loading version history...</p> : null}
      {query.error ? <ErrorNotice error={query.error} /> : null}
      {duplicate ? (
        <ErrorNotice
          error={
            new Error(
              "Version history repeated an immutable version across pages. Refresh before comparing."
            )
          }
        />
      ) : null}
      {versions && !duplicate ? (
        <>
          <p className="break-all">
            Active version: {latestPage?.activeVersionId ?? "None"}.
          </p>
          <p className="break-all">
            Latest version: {latestPage?.latestVersionId ?? "None"}.
          </p>
          {versions.length === 0 ? (
            <p role="status">No registered versions.</p>
          ) : (
            <>
              <form
                aria-label="Compare workflow versions"
                onSubmit={compare}
                className="flex min-w-0 flex-wrap items-end gap-3"
              >
                {(["from", "to"] as const).map((side) => {
                  const selected = side === "from" ? selectedFrom : selectedTo
                  return (
                    <label
                      key={side}
                      className="flex max-w-full min-w-0 flex-col gap-1 overflow-hidden"
                    >
                      Compare {side}
                      <select
                        value={selected}
                        onChange={(event) =>
                          (side === "from" ? setFrom : setTo)(
                            event.target.value
                          )
                        }
                        className="max-w-full min-w-0 rounded border bg-background p-2 focus-visible:outline-2 focus-visible:-outline-offset-2"
                      >
                        {selected &&
                        !versions.some(
                          (version) => version.versionId === selected
                        ) ? (
                          <option value={selected}>
                            {selected === pinnedVersionId
                              ? "Pinned"
                              : "Selected"}{" "}
                            version {selected}
                          </option>
                        ) : null}
                        {versions.map((version) => (
                          <option
                            key={version.versionId}
                            value={version.versionId}
                          >
                            {version.version} ({version.versionId})
                          </option>
                        ))}
                      </select>
                    </label>
                  )
                })}
                <Button type="submit" disabled={!selectedFrom || !selectedTo}>
                  Compare versions
                </Button>
              </form>
              <div
                role="region"
                aria-label="Version history table"
                tabIndex={0}
                className="overflow-x-auto rounded-md border"
              >
                <table className="w-full text-left text-sm">
                  <caption className="sr-only">
                    Immutable versions, registration provenance and change notes
                  </caption>
                  <thead className="border-b">
                    <tr>
                      {[
                        "Version",
                        "Registered (UTC)",
                        "Git author / Registered by",
                        "Commit / SDK",
                        "Change note",
                        "Scaffold",
                      ].map((title) => (
                        <th key={title} scope="col" className="p-3">
                          {title}
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {versions.map((version) => (
                      <tr
                        key={version.versionId}
                        className="border-b last:border-0"
                      >
                        <th scope="row" className="p-3 font-normal">
                          <Link
                            to="/t/$team/$type/w/$wf/versions/$v"
                            params={{ team, type, wf, v: version.versionId }}
                          >
                            {version.version}
                          </Link>
                          <p className="max-w-72 text-xs break-all">
                            {version.versionId}
                          </p>
                          {latestPage?.activeVersionId === version.versionId ? (
                            <p>Active</p>
                          ) : null}
                          {latestPage?.latestVersionId === version.versionId ? (
                            <p>Latest</p>
                          ) : null}
                        </th>
                        <td className="p-3">
                          <time dateTime={version.registration.registeredAt}>
                            {version.registration.registeredAt}
                          </time>
                        </td>
                        <td className="max-w-80 p-3 break-words">
                          <p>{version.registration.gitAuthor}</p>
                          <p>{version.registration.registeredBy}</p>
                        </td>
                        <td className="max-w-72 p-3 break-all">
                          <p className="font-mono text-xs">
                            {version.registration.gitCommit}
                          </p>
                          <p>SDK {version.registration.sdkVersion}</p>
                        </td>
                        <td className="max-w-96 p-3 break-words whitespace-pre-wrap">
                          {version.registration.changeNote ?? "No change note."}
                        </td>
                        <td className="p-3">
                          {version.versionId !== pinnedVersionId ? (
                            <CloneDialog
                              workflowId={workflowId}
                              versionId={version.versionId}
                            />
                          ) : (
                            <span>Pinned version above</span>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </>
          )}
          {query.hasNextPage ? (
            <Button
              variant="outline"
              disabled={query.isFetching}
              onClick={() => void query.fetchNextPage()}
            >
              {query.isFetchingNextPage
                ? "Loading more versions..."
                : "Load more versions"}
            </Button>
          ) : null}
          {comparison ? (
            <VersionComparison
              key={`${comparison.from}/${comparison.to}`}
              workflowId={workflowId}
              {...comparison}
            />
          ) : null}
        </>
      ) : null}
    </section>
  )
}
