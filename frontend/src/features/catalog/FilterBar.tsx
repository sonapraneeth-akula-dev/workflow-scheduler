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
      className="space-y-4"
    >
      <p className="text-sm text-muted-foreground">
        Search exact identifiers and copied run metadata. All filters combine
        with AND. Times require an explicit offset; the end is exclusive.
      </p>
      <div className="grid gap-3 sm:grid-cols-2">
        {team ? <input type="hidden" name="team" value={team} /> : null}
        {(
          [
            ["team", "Team"],
            ["type", "Type"],
            ["workflowId", "Workflow ID"],
            ["seriesId", "Series ID"],
            ["from", "Created from (inclusive RFC3339)"],
            ["to", "Created before (exclusive RFC3339)"],
          ] as const
        )
          .filter(([name]) => name !== "team" || !team)
          .map(([name, label]) => (
            <label key={name} className="flex min-w-0 flex-col gap-1">
              {label}
              <Input
                name={name}
                defaultValue={filters[name] ?? ""}
                autoComplete="off"
              />
            </label>
          ))}
        <label className="flex min-w-0 flex-col gap-1">
          Run status
          <NativeSelect name="status" defaultValue={filters.status ?? ""}>
            <option value="">All statuses</option>
            {runStatuses.map((status) => (
              <option key={status}>{status}</option>
            ))}
          </NativeSelect>
        </label>
        <label className="flex min-w-0 flex-col gap-1">
          Group loaded runs by
          <NativeSelect name="groupBy" defaultValue={filters.groupBy ?? "none"}>
            <option value="none">No grouping</option>
            <option value="team">Team</option>
            <option value="type">Type</option>
            <option value="workflow">Workflow</option>
            <option value="status">Status</option>
            <option value="metadata">Metadata</option>
          </NativeSelect>
        </label>
        <label className="flex min-w-0 flex-col gap-1">
          Metadata group key
          <Input
            name="groupKey"
            defaultValue={filters.groupKey ?? ""}
            maxLength={128}
          />
        </label>
        <label className="flex min-w-0 flex-col gap-1">
          Metadata filters (JSON)
          <textarea
            name="metadata"
            defaultValue={
              filters.metadata ? JSON.stringify(filters.metadata) : ""
            }
            maxLength={65536}
            rows={3}
            className="w-full min-w-0 rounded-md border bg-background px-3 py-2 font-mono text-sm"
            placeholder={'{"environment":"prod"}'}
          />
        </label>
      </div>
      {error ? <p role="alert">{error}</p> : null}
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
    </form>
  )
}
