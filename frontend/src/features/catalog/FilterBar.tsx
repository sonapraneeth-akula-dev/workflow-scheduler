import { useId, useState, type FormEvent } from "react"
import { runStatuses } from "./api"
import { Button } from "@/components/ui/button"
import { NativeSelect } from "@/components/ui/native-select"
import { Input } from "@/components/ui/input"
import { runSearchSchema, type RunSearch } from "@/features/runs/search"
import { useRunFilterSuggestions } from "@/features/runs/useRunFilterSuggestions"
import { ErrorNotice } from "@/components/ErrorNotice"

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
  const [type, setType] = useState(filters.type ?? "")
  const [workflowId, setWorkflowId] = useState(filters.workflowId ?? "")
  const [metadataDraft, setMetadataDraft] = useState(
    filters.metadata ? JSON.stringify(filters.metadata) : ""
  )
  const [metadataKey, setMetadataKey] = useState("")
  const [metadataValue, setMetadataValue] = useState("")
  const options = useRunFilterSuggestions(team ?? filters.team, type)
  const id = useId()
  function addMetadataFilter() {
    let current: unknown
    try {
      current = metadataDraft.trim() ? JSON.parse(metadataDraft) : {}
    } catch (error) {
      if (!(error instanceof SyntaxError)) throw error
      setError("Metadata filters must be a JSON object of exact string values.")
      return
    }
    const existing = runSearchSchema.safeParse({ metadata: current })
    if (!existing.success) {
      setError(existing.error.issues.map((issue) => issue.message).join(" "))
      return
    }
    const parsed = runSearchSchema.safeParse({
      metadata: { ...existing.data.metadata, [metadataKey]: metadataValue },
    })
    if (!parsed.success) {
      setError(parsed.error.issues.map((issue) => issue.message).join(" "))
      return
    }
    setError(null)
    setMetadataDraft(JSON.stringify(parsed.data.metadata))
    setMetadataKey("")
    setMetadataValue("")
  }
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
                {...(name === "type"
                  ? {
                      value: type,
                      list: `${id}-types`,
                      placeholder: "Choose or enter type",
                      onChange: (event) => {
                        setType(event.target.value)
                        if (
                          workflowId &&
                          workflowId.split("/")[1] !== event.target.value
                        )
                          setWorkflowId("")
                      },
                    }
                  : name === "workflowId"
                    ? {
                        value: workflowId,
                        list: `${id}-workflows`,
                        placeholder: "Choose or enter workflow ID",
                        onChange: (event) => setWorkflowId(event.target.value),
                      }
                    : { defaultValue: filters[name] ?? "" })}
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
        <datalist id={`${id}-types`}>
          {options.types.map((entry) => (
            <option key={entry.name} value={entry.name}>
              {entry.description}
            </option>
          ))}
        </datalist>
        <datalist id={`${id}-workflows`}>
          {options.workflows.map((entry) => (
            <option key={entry.id} value={entry.id}>
              {entry.name}
            </option>
          ))}
        </datalist>
        <datalist id={`${id}-metadata-keys`}>
          {[...options.metadata.keys()].sort().map((key) => (
            <option key={key} value={key} />
          ))}
        </datalist>
        <datalist id={`${id}-metadata-values`}>
          {[...(options.metadata.get(metadataKey) ?? [])]
            .sort()
            .map((value) => (
              <option key={value} value={value} />
            ))}
        </datalist>
        {options.error ? (
          <div className="space-y-2">
            <ErrorNotice error={options.error} />
            <Button
              type="button"
              variant="outline"
              disabled={options.loading}
              onClick={options.retry}
            >
              Retry filter suggestions
            </Button>
          </div>
        ) : null}
        {options.hasMore ? (
          <Button
            type="button"
            variant="outline"
            disabled={options.loading}
            onClick={options.loadMore}
          >
            Load more workflow suggestions
          </Button>
        ) : null}
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
              list={`${id}-metadata-keys`}
              defaultValue={filters.groupKey ?? ""}
              maxLength={128}
            />
          </label>
          <label className="flex min-w-0 flex-col gap-1 text-sm">
            Metadata filters (JSON)
            <textarea
              name="metadata"
              value={metadataDraft}
              onChange={(event) => setMetadataDraft(event.target.value)}
              maxLength={65536}
              rows={2}
              className="w-full min-w-0 rounded-md border bg-background px-3 py-2 font-mono text-sm"
              placeholder={'{"environment":"prod"}'}
            />
          </label>
          <div className="grid min-w-0 grid-cols-2 items-end gap-2 sm:col-span-2 lg:col-span-3 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto]">
            <label className="flex min-w-0 flex-col gap-1 text-sm">
              Metadata key
              <Input
                value={metadataKey}
                onChange={(event) => {
                  setMetadataKey(event.target.value)
                  setMetadataValue("")
                }}
                list={`${id}-metadata-keys`}
                maxLength={128}
              />
            </label>
            <label className="flex min-w-0 flex-col gap-1 text-sm">
              Metadata value
              <Input
                value={metadataValue}
                onChange={(event) => setMetadataValue(event.target.value)}
                list={`${id}-metadata-values`}
                maxLength={1024}
              />
            </label>
            <Button
              type="button"
              variant="outline"
              disabled={!metadataKey}
              onClick={addMetadataFilter}
            >
              Add metadata filter
            </Button>
          </div>
        </div>
        <p className="mt-2 text-xs text-muted-foreground">
          Exact matches, combined with AND. Times require an RFC3339 offset; the
          end is exclusive. Choose catalogue suggestions or enter custom values.
          Suggestions use current workflow metadata; searches match each run's
          recorded metadata.
        </p>
      </details>
      {error ? <p role="alert">{error}</p> : null}
    </form>
  )
}
