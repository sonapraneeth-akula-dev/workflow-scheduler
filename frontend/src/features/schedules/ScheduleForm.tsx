import { useState } from "react"
import { useIsMutating, useMutation, useQuery } from "@tanstack/react-query"
import { Button } from "@/components/ui/button"
import { Can } from "@/components/ui/Can"
import {
  Field,
  FieldDescription,
  FieldError,
  FieldGroup,
  FieldLabel,
  FieldLegend,
  FieldSet,
} from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select"
import { Textarea } from "@/components/ui/textarea"
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group"
import { ErrorNotice } from "@/components/ErrorNotice"
import { formatTimestamp } from "@/lib/timestamps"
import { usePermissions } from "@/lib/auth/usePermissions"
import {
  ApiError,
  previewSchedule,
  saveSchedule,
  type ScheduleSnapshot,
} from "@/lib/api/client"
import {
  draftFor,
  timezones,
  validateDraft,
  type ScheduleDraft,
} from "./schedule-draft"

interface ScheduleFormProps {
  readonly current?: ScheduleSnapshot
  readonly workflowId?: string
  readonly onSaved: (snapshot: ScheduleSnapshot) => void
}

export function ScheduleForm({
  current,
  workflowId,
  onSaved,
}: ScheduleFormProps) {
  const [draft, setDraft] = useState<ScheduleDraft>(() => ({
    ...draftFor(current?.schedule),
    workflowId: current?.schedule.workflowId ?? workflowId ?? "",
  }))
  const validation = validateDraft(draft)
  const permissions = usePermissions(
    validation.errors.has("workflowId")
      ? undefined
      : { scope: "type", id: draft.workflowId.split("/").slice(0, 2).join("/") }
  )
  const canWrite = permissions.can("Operator")
  const request = validation.request
  const preview = useQuery({
    queryKey: ["schedule-preview", request],
    enabled: request !== undefined && canWrite,
    staleTime: 0,
    gcTime: 0,
    queryFn: ({ signal }) => {
      if (!request) {
        throw new Error("A valid schedule is required for preview.")
      }
      return previewSchedule(request, signal)
    },
  })
  const save = useMutation({
    mutationKey: ["schedule-write", current?.schedule.id ?? "new"],
    mutationFn: async () => {
      if (!permissions.can("Operator")) {
        throw new Error(
          "Operator permission is required to manage this schedule."
        )
      }
      if (!request || !preview.isSuccess) {
        throw new Error(
          "Resolve validation errors and preview the next five fires before saving."
        )
      }
      return saveSchedule(request, current)
    },
    onSuccess: onSaved,
  })
  const isWriting =
    useIsMutating({
      mutationKey: ["schedule-write", current?.schedule.id ?? "new"],
    }) > 0
  function update<Key extends keyof ScheduleDraft>(
    key: Key,
    value: ScheduleDraft[Key]
  ) {
    setDraft((previous) => ({ ...previous, [key]: value }))
    if (!(save.error instanceof ApiError && save.error.status === 412)) {
      save.reset()
    }
  }
  function errorFor(key: keyof ScheduleDraft): string | undefined {
    const path = "$." + (key === "seconds" ? "interval" : key)
    return (
      validation.errors.get(key) ??
      (preview.error instanceof ApiError
        ? (preview.error.fields.get(path) ??
          (key === "cron" || key === "seconds"
            ? preview.error.fields.get("$")
            : undefined))
        : undefined) ??
      (save.error instanceof ApiError ? save.error.fields.get(path) : undefined)
    )
  }
  const hasConflict =
    save.error instanceof ApiError && save.error.status === 412
  return (
    <form
      aria-label="Schedule"
      noValidate
      onSubmit={(event) => {
        event.preventDefault()
        save.mutate()
      }}
      className="flex flex-col gap-6"
    >
      <fieldset disabled={isWriting} className="contents">
        <FieldGroup>
          <Field data-invalid={Boolean(errorFor("workflowId"))}>
            <FieldLabel htmlFor="workflowId">Workflow identity</FieldLabel>
            <Input
              id="workflowId"
              value={draft.workflowId}
              readOnly={Boolean(current)}
              placeholder="team/type/workflow"
              aria-invalid={Boolean(errorFor("workflowId"))}
              aria-describedby="workflowId-error"
              onChange={(event) => update("workflowId", event.target.value)}
            />
            <FieldError id="workflowId-error">
              {errorFor("workflowId")}
            </FieldError>
          </Field>
          <Field data-invalid={Boolean(errorFor("name"))}>
            <FieldLabel htmlFor="name">Schedule name</FieldLabel>
            <Input
              id="name"
              value={draft.name}
              aria-invalid={Boolean(errorFor("name"))}
              aria-describedby="name-error"
              onChange={(event) => update("name", event.target.value)}
            />
            <FieldError id="name-error">{errorFor("name")}</FieldError>
          </Field>
          <Field data-invalid={Boolean(errorFor("timezone"))}>
            <FieldLabel htmlFor="timezone">Timezone</FieldLabel>
            <NativeSelect
              id="timezone"
              value={draft.timezone}
              aria-invalid={Boolean(errorFor("timezone"))}
              aria-describedby="timezone-help timezone-error"
              onChange={(event) => update("timezone", event.target.value)}
            >
              {timezones.includes(draft.timezone) ? null : (
                <NativeSelectOption value={draft.timezone}>
                  {draft.timezone}
                </NativeSelectOption>
              )}
              {timezones.map((zone) => (
                <NativeSelectOption key={zone} value={zone}>
                  {zone}
                </NativeSelectOption>
              ))}
            </NativeSelect>
            <FieldDescription id="timezone-help">
              Cron uses this timezone, including daylight-saving transitions.
              Intervals measure elapsed seconds in UTC.
            </FieldDescription>
            <FieldError id="timezone-error">{errorFor("timezone")}</FieldError>
          </Field>
          <FieldSet>
            <FieldLegend>Expression</FieldLegend>
            <ToggleGroup
              type="single"
              variant="outline"
              value={draft.expression}
              aria-label="Expression type"
              onValueChange={(value) => {
                if (value === "cron" || value === "interval") {
                  update("expression", value)
                }
              }}
            >
              <ToggleGroupItem value="cron">Cron</ToggleGroupItem>
              <ToggleGroupItem value="interval">Interval</ToggleGroupItem>
            </ToggleGroup>
            {draft.expression === "cron" ? (
              <Field data-invalid={Boolean(errorFor("cron"))}>
                <FieldLabel htmlFor="cron">Cron expression</FieldLabel>
                <Input
                  id="cron"
                  value={draft.cron}
                  aria-invalid={Boolean(errorFor("cron"))}
                  aria-describedby="cron-help cron-error"
                  onChange={(event) => update("cron", event.target.value)}
                />
                <FieldDescription id="cron-help">
                  Minute, hour, day of month, month, day of week. Example: 0 9 *
                  * 1-5 (weekdays at 09:00).
                </FieldDescription>
                <FieldError id="cron-error">{errorFor("cron")}</FieldError>
              </Field>
            ) : (
              <Field data-invalid={Boolean(errorFor("seconds"))}>
                <FieldLabel htmlFor="seconds">Interval seconds</FieldLabel>
                <Input
                  id="seconds"
                  type="number"
                  min={1}
                  max={2_147_483_647}
                  step={1}
                  value={draft.seconds}
                  aria-invalid={Boolean(errorFor("seconds"))}
                  aria-describedby="seconds-error"
                  onChange={(event) => update("seconds", event.target.value)}
                />
                <FieldError id="seconds-error">
                  {errorFor("seconds")}
                </FieldError>
              </Field>
            )}
          </FieldSet>
          <Field data-invalid={Boolean(errorFor("versionPin"))}>
            <FieldLabel htmlFor="versionPin">Version pin (optional)</FieldLabel>
            <Input
              id="versionPin"
              value={draft.versionPin}
              aria-describedby="versionPin-help versionPin-error"
              aria-invalid={Boolean(errorFor("versionPin"))}
              onChange={(event) => update("versionPin", event.target.value)}
            />
            <FieldDescription id="versionPin-help">
              Leave empty for the active version. Or use a version UUID, exact
              SemVer, comparison range, or wildcard.
            </FieldDescription>
            <FieldError id="versionPin-error">
              {errorFor("versionPin")}
            </FieldError>
          </Field>
          <FieldSet>
            <FieldLegend>Overlap policy</FieldLegend>
            <ToggleGroup
              type="single"
              variant="outline"
              value={draft.overlap}
              aria-label="Overlap policy"
              onValueChange={(value) => {
                if (
                  value === "Skip" ||
                  value === "Queue" ||
                  value === "Concurrent"
                ) {
                  update("overlap", value)
                }
              }}
            >
              <ToggleGroupItem value="Skip">Skip</ToggleGroupItem>
              <ToggleGroupItem value="Queue">Queue</ToggleGroupItem>
              <ToggleGroupItem value="Concurrent">
                Allow concurrent
              </ToggleGroupItem>
            </ToggleGroup>
          </FieldSet>
          <FieldSet>
            <FieldLegend>Outage catch-up</FieldLegend>
            <ToggleGroup
              type="single"
              variant="outline"
              value={draft.catchUp}
              aria-label="Outage catch-up"
              onValueChange={(value) => {
                if (value === "None" || value === "Latest") {
                  update("catchUp", value)
                }
              }}
            >
              <ToggleGroupItem value="Latest">Latest only</ToggleGroupItem>
              <ToggleGroupItem value="None">None</ToggleGroupItem>
            </ToggleGroup>
          </FieldSet>
          <Field data-invalid={Boolean(errorFor("variables"))}>
            <FieldLabel htmlFor="variables">
              Variable overrides (JSON object)
            </FieldLabel>
            <Textarea
              id="variables"
              value={draft.variables}
              rows={4}
              aria-invalid={Boolean(errorFor("variables"))}
              aria-describedby="variables-help variables-error"
              onChange={(event) => update("variables", event.target.value)}
            />
            <FieldDescription id="variables-help">
              Defaults come from the selected workflow version. Supply secret
              references, never secret values.
            </FieldDescription>
            <FieldError id="variables-error">
              {errorFor("variables")}
            </FieldError>
          </Field>
        </FieldGroup>
      </fieldset>
      <section aria-labelledby="next-fires" aria-live="polite">
        <h2 id="next-fires" className="mb-3 text-lg font-semibold">
          Next 5 fire times
        </h2>
        <p className="mb-3 text-sm text-muted-foreground">
          Times shown in your local timezone. The schedule timezone above
          controls cron evaluation.
        </p>
        {!request || !canWrite ? (
          <p>Complete the valid schedule fields to preview upcoming fires.</p>
        ) : null}
        {preview.isFetching ? (
          <p role="status">Calculating fire times...</p>
        ) : null}
        {request && canWrite && preview.fetchStatus === "paused" ? (
          <ErrorNotice
            error={
              new Error(
                "Network unavailable. Preview paused; reconnect before saving."
              )
            }
          />
        ) : null}
        {request && canWrite && preview.error ? (
          <ErrorNotice error={preview.error} />
        ) : null}
        {request && canWrite && preview.isSuccess ? (
          <ol className="flex list-inside list-decimal flex-col gap-2">
            {preview.data.map((fire) => (
              <li key={fire}>
                <time dateTime={fire} title={fire}>
                  {formatTimestamp(fire)}
                </time>
              </li>
            ))}
          </ol>
        ) : null}
      </section>
      {permissions.isFetching ? (
        <p role="status">Checking permissions...</p>
      ) : null}
      {permissions.error ? <ErrorNotice error={permissions.error} /> : null}
      {permissions.fetchStatus === "paused" ? (
        <ErrorNotice
          error={
            new Error(
              "Permission check paused. Reconnect before managing this schedule."
            )
          }
        />
      ) : null}
      {permissions.isSuccess && !permissions.isFetching && !canWrite ? (
        <p role="status">
          Read-only: Operator permission is required to manage schedules.
        </p>
      ) : null}
      {save.error ? <ErrorNotice error={save.error} /> : null}
      <Can permissions={permissions} role="Operator">
        <Button
          type="submit"
          disabled={!request || !preview.isSuccess || isWriting || hasConflict}
        >
          {save.isPending
            ? "Saving..."
            : current
              ? "Save changes"
              : "Create schedule"}
        </Button>
      </Can>
    </form>
  )
}
