import { useState, type FormEvent } from "react"
import { useInfiniteQuery } from "@tanstack/react-query"
import { ErrorNotice } from "@/components/ErrorNotice"
import { formatTimestamp } from "@/lib/timestamps"
import { Button } from "@/components/ui/button"
import {
  auditSearchSchema,
  getAudit,
  type AuditCursor,
  type AuditSearch,
} from "./api"

export function AuditFilters({
  search,
  onChange,
}: {
  search: AuditSearch
  onChange: (next: AuditSearch) => void
}) {
  const [error, setError] = useState<string>()
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const fields: Record<string, string> = {}
    new FormData(event.currentTarget).forEach((value, key) => {
      if (typeof value === "string" && value !== "")
        fields[key] =
          key === "from" || key === "to" ? value + "T00:00:00Z" : value
    })
    const parsed = auditSearchSchema.safeParse(fields)
    if (!parsed.success) {
      setError(parsed.error.issues.map((issue) => issue.message).join(" "))
      return
    }
    setError(undefined)
    onChange(parsed.data)
  }
  return (
    <form
      aria-label="Audit filters"
      onSubmit={submit}
      className="flex min-w-0 flex-wrap items-end gap-3"
    >
      {(["actor", "targetKind", "targetId"] as const).map((name) => (
        <label key={name} className="flex min-w-0 flex-col gap-1">
          {
            {
              actor: "Actor (exact)",
              targetKind: "Target kind (exact)",
              targetId: "Target ID (exact)",
            }[name]
          }
          <input
            name={name}
            defaultValue={search[name] ?? ""}
            maxLength={1024}
            className="min-w-0 rounded border bg-background p-2"
          />
        </label>
      ))}
      {(["from", "to"] as const).map((name) => (
        <label key={name} className="flex min-w-0 flex-col gap-1">
          {name === "from"
            ? "From date (UTC, inclusive)"
            : "Until date (UTC, exclusive)"}
          <input
            name={name}
            type="date"
            defaultValue={
              search[name]
                ? new Date(search[name]).toISOString().slice(0, 10)
                : ""
            }
            className="min-w-0 rounded border bg-background p-2"
          />
        </label>
      ))}
      <Button type="submit">Filter audit</Button>
      <Button type="button" variant="outline" onClick={() => onChange({})}>
        Clear audit filters
      </Button>
      {error ? <p role="alert">{error}</p> : null}
    </form>
  )
}

export function AuditTrail({ search }: { search: AuditSearch }) {
  const query = useInfiniteQuery({
    queryKey: ["audit", search],
    initialPageParam: null as AuditCursor,
    queryFn: ({ pageParam, signal }) => getAudit(search, pageParam, signal),
    getNextPageParam: (page): AuditCursor | undefined =>
      page.nextCursor
        ? { cursor: page.nextCursor, after: page.items.at(-1)! }
        : undefined,
  })
  const rows = query.error
    ? undefined
    : query.data?.pages.flatMap((page) => page.items)
  return (
    <section aria-label="Audit records" className="min-w-0 space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p>
          Global Admin only. Newest first; summaries contain committed audit
          metadata.
        </p>
        <Button
          variant="outline"
          disabled={query.isFetching}
          onClick={() => void query.refetch()}
        >
          Refresh audit
        </Button>
      </div>
      {query.isPending ? <p role="status">Loading audit trail...</p> : null}
      {query.error ? <ErrorNotice error={query.error} /> : null}
      {rows?.length === 0 ? <p>No matching audit records.</p> : null}
      {rows?.length ? (
        <div
          className="max-w-full overflow-x-auto"
          role="region"
          aria-label="Audit table"
          tabIndex={0}
        >
          <table className="w-full text-left text-sm">
            <caption className="sr-only">Audit trail, newest first</caption>
            <thead>
              <tr>
                {[
                  "Timestamp (local)",
                  "Actor",
                  "Action",
                  "Target",
                  "Summary",
                ].map((title) => (
                  <th key={title} scope="col" className="p-2">
                    {title}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <tr key={`${row.at}/${row.id}`} className="border-t align-top">
                  <td className="p-2">
                    <time dateTime={row.at} title={row.at}>
                      {formatTimestamp(row.at)}
                    </time>
                    <p>#{row.id}</p>
                  </td>
                  <td className="max-w-56 p-2 break-words">
                    {row.actor}
                    <p>{row.actorKind}</p>
                  </td>
                  <td className="max-w-48 p-2 break-words">{row.action}</td>
                  <td className="max-w-56 p-2 break-words">
                    {row.targetKind}
                    <p>{row.targetId}</p>
                    {row.teamId ? <p>Team: {row.teamId}</p> : null}
                  </td>
                  <td className="p-2">
                    <pre className="max-w-80 break-words whitespace-pre-wrap">
                      {JSON.stringify(row.summary, null, 2)}
                    </pre>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}
      {rows && query.hasNextPage ? (
        <Button
          variant="outline"
          disabled={query.isFetching}
          onClick={() => void query.fetchNextPage()}
        >
          {query.isFetchingNextPage
            ? "Loading audit records..."
            : "Load more audit records"}
        </Button>
      ) : null}
    </section>
  )
}
