import { useState, type FormEvent } from "react"
import { useInfiniteQuery } from "@tanstack/react-query"
import { Dialog } from "radix-ui"
import { ErrorNotice } from "@/components/ErrorNotice"
import { formatTimestamp } from "@/lib/timestamps"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
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
          <Input
            name={name}
            defaultValue={search[name] ?? ""}
            maxLength={1024}
          />
        </label>
      ))}
      {(["from", "to"] as const).map((name) => (
        <label key={name} className="flex min-w-0 flex-col gap-1">
          {name === "from"
            ? "From date (UTC, inclusive)"
            : "Until date (UTC, exclusive)"}
          <Input
            name={name}
            type="date"
            defaultValue={
              search[name]
                ? new Date(search[name]).toISOString().slice(0, 10)
                : ""
            }
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
          Global Admin only. This audit trail records who performed an action,
          when it was committed, and which target it affected, newest first.
          View summary opens the recorded metadata, including before/after
          values when available. These are administrative and control actions,
          not workflow execution results or task output.
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
                    <Dialog.Root>
                      <Dialog.Trigger asChild>
                        <Button
                          variant="outline"
                          size="sm"
                          className="whitespace-nowrap"
                          aria-label={`View summary for audit record #${row.id}`}
                        >
                          View summary
                        </Button>
                      </Dialog.Trigger>
                      <Dialog.Portal>
                        <Dialog.Overlay className="fixed inset-0 z-50 bg-foreground/30" />
                        <Dialog.Content className="fixed top-1/2 left-1/2 z-50 flex max-h-[calc(100dvh-2rem)] w-[calc(100%-2rem)] max-w-3xl -translate-x-1/2 -translate-y-1/2 flex-col gap-4 overflow-y-auto rounded-lg border bg-background p-5 text-foreground shadow-lg">
                          <Dialog.Title className="text-xl font-semibold">
                            Audit record #{row.id}
                          </Dialog.Title>
                          <Dialog.Description className="text-sm break-words text-muted-foreground">
                            Committed summary for {row.action} on{" "}
                            {row.targetKind}: {row.targetId}. Recorded values
                            only; missing fields are not inferred.
                          </Dialog.Description>
                          <p className="text-sm break-words">
                            {row.actor} ({row.actorKind}){" "}
                            <time dateTime={row.at} title={row.at}>
                              {formatTimestamp(row.at)}
                            </time>
                          </p>
                          <pre
                            role="region"
                            aria-label="Audit summary JSON"
                            tabIndex={0}
                            className="max-h-[50dvh] min-h-0 shrink overflow-auto rounded border bg-muted p-3 text-sm break-words whitespace-pre-wrap"
                          >
                            {JSON.stringify(row.summary, null, 2)}
                          </pre>
                          <Dialog.Close asChild>
                            <Button variant="outline" className="self-end">
                              Close summary
                            </Button>
                          </Dialog.Close>
                        </Dialog.Content>
                      </Dialog.Portal>
                    </Dialog.Root>
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
