import { useState, type FormEvent } from "react"
import { runStatuses } from "./api"
import { Button } from "@/components/ui/button"
import { NativeSelect } from "@/components/ui/native-select"
import { Input } from "@/components/ui/input"
import { runSearchSchema, type RunSearch } from "@/features/runs/search"

export function FilterBar({
  filters,
  onChange,
  team,
}: {
  filters: RunSearch
  onChange: (filters: RunSearch) => void
  team?: string
}) {
  const [error, setError] = useState<string | null>(null)
  const advancedCount = [
    filters.seriesId,
    filters.from,
    filters.to,
    filters.groupBy && filters.groupBy !== "none",
    filters.groupKey,
    ...Object.keys(filters.metadata ?? {}),
  ].filter(Boolean).length
  const [advancedOpen, setAdvancedOpen] = useState(advancedCount > 0)
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const form = new FormData(event.currentTarget)
    let metadata: unknown
    try {
      const value = form.get("metadata")
      metadata =
        typeof value === "string" && value.trim()
          ? JSON.parse(value)
          : undefined
    } catch (error) {
      if (!(error instanceof SyntaxError)) throw error
      setAdvancedOpen(true)
      setError("Metadata filters must be a JSON object of exact string values.")
      return
    }
    const fields: Record<string, unknown> = {}
    form.forEach((value, key) => {
      fields[key] = value === "" ? undefined : value
    })
    const parsed = runSearchSchema.safeParse({
      ...fields,
      ...(team ? { team } : {}),
      metadata,
    })
    if (!parsed.success) {
      setAdvancedOpen(true)
      setError(
        parsed.error.issues
          .map((issue) => `${issue.path.join(".")}: ${issue.message}`)
          .join(" ")
      )
      return
    }
    setError(null)
    onChange(parsed.data)
  }
  return (
    <form
      aria-label="Run filters"
      onSubmit={submit}
      onInput={() => setError(null)}
      className="min-w-0 space-y-3 rounded-md border bg-background p-3"
    >
      {team ? <input type="hidden" name="team" value={team} /> : null}
      <div className="grid items-end gap-3 sm:grid-cols-2 xl:grid-cols-[minmax(8rem,1fr)_minmax(12rem,2fr)_minmax(10rem,1fr)_auto]">
        {(
          [
            ["team", "Team"],
            ["type", "Type"],
            ["workflowId", "Workflow ID"],
          ] as const
        )
          .filter(([name]) => name !== "team" || !team)
          .map(([name, label]) => (
            <label key={name} className="flex min-w-0 flex-col gap-1 text-sm">
              {label}
              <Input
                name={name}
                defaultValue={filters[name] ?? ""}
                autoComplete="off"
              />
            </label>
          ))}
        <label className="flex min-w-0 flex-col gap-1 text-sm">
          Run status
          <NativeSelect
            name="status"
            defaultValue={filters.status ?? ""}
            className="h-8"
          >
            <option value="">All statuses</option>
            {runStatuses.map((status) => (
              <option key={status}>{status}</option>
            ))}
          </NativeSelect>
        </label>
        <div className="flex flex-wrap gap-2">
          <Button type="submit">Search runs</Button>
          <Button
            type="button"
            variant="outline"
            onClick={() => onChange(team ? { team } : {})}
          >
            Clear filters
          </Button>
        </div>
      </div>
      <details
        open={advancedOpen}
        onToggle={(event) => setAdvancedOpen(event.currentTarget.open)}
      >
        <summary className="w-fit text-sm font-medium">
          Advanced filters{advancedCount ? ` (${advancedCount} active)` : ""}
        </summary>
        <div className="mt-3 grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {(
            [
              ["seriesId", "Series ID"],
              ["from", "Created from (inclusive RFC3339)"],
              ["to", "Created before (exclusive RFC3339)"],
            ] as const
          ).map(([name, label]) => (
            <label key={name} className="flex min-w-0 flex-col gap-1 text-sm">
              {label}
              <Input
                name={name}
                defaultValue={filters[name] ?? ""}
                autoComplete="off"
              />
            </label>
          ))}
          <label className="flex min-w-0 flex-col gap-1 text-sm">
            Group loaded runs by
            <NativeSelect
              name="groupBy"
              defaultValue={filters.groupBy ?? "none"}
            >
              <option value="none">No grouping</option>
              <option value="team">Team</option>
              <option value="type">Type</option>
              <option value="workflow">Workflow</option>
              <option value="status">Status</option>
              <option value="metadata">Metadata</option>
            </NativeSelect>
          </label>
          <label className="flex min-w-0 flex-col gap-1 text-sm">
            Metadata group key
            <Input
              name="groupKey"
              defaultValue={filters.groupKey ?? ""}
              maxLength={128}
            />
          </label>
          <label className="flex min-w-0 flex-col gap-1 text-sm">
            Metadata filters (JSON)
            <textarea
              name="metadata"
              defaultValue={
                filters.metadata ? JSON.stringify(filters.metadata) : ""
              }
              maxLength={65536}
              rows={2}
              className="w-full min-w-0 rounded-md border bg-background px-3 py-2 font-mono text-sm"
              placeholder={'{"environment":"prod"}'}
            />
          </label>
        </div>
        <p className="mt-2 text-xs text-muted-foreground">
          Exact matches, combined with AND. Times require an RFC3339 offset; the
          end is exclusive.
        </p>
      </details>
      {error ? <p role="alert">{error}</p> : null}
    </form>
  )
}
