import { useId } from "react"
import {
  Field,
  FieldDescription,
  FieldGroup,
  FieldLabel,
  FieldLegend,
  FieldSet,
} from "@/components/ui/field"
import {
  scheduleName,
  trackingDescription,
  type ScheduleTrackingSelection,
  type TrackedSchedule,
} from "./tracking"

export function ScheduleTracking({
  workflowId,
  schedules,
  selection,
  onChange,
}: {
  workflowId: string
  schedules: readonly TrackedSchedule[]
  selection: ScheduleTrackingSelection
  onChange: (next: ScheduleTrackingSelection) => void
}) {
  const id = useId()
  const selectedIds =
    selection.seriesIds ?? schedules.map((item) => item.seriesId)
  const choices = [
    ...new Set([...schedules.map((item) => item.seriesId), ...selectedIds]),
  ]
  return (
    <div className="flex min-w-0 flex-col gap-2">
      <p
        className="text-sm break-words"
        aria-label={`Tracking for ${workflowId}`}
      >
        <span className="font-medium">Tracking:</span>{" "}
        {trackingDescription(schedules, selection)}
      </p>
      <details>
        <summary className="w-fit cursor-pointer text-sm text-primary">
          Choose tracked schedules
        </summary>
        <FieldSet className="mt-3 min-w-0">
          <FieldLegend variant="label">
            Tracked schedules for {workflowId}
          </FieldLegend>
          <FieldDescription>
            Personal view, saved in this browser. Does not pause or change
            schedules.
          </FieldDescription>
          <FieldGroup>
            <Field orientation="horizontal">
              <input
                id={`${id}-all`}
                type="checkbox"
                checked={selection.seriesIds === null}
                onChange={(event) =>
                  onChange({
                    ...selection,
                    seriesIds: event.target.checked ? null : [],
                  })
                }
              />
              <FieldLabel htmlFor={`${id}-all`}>
                All schedules, including future schedules
              </FieldLabel>
            </Field>
            {choices.map((seriesId) => {
              const schedule = schedules.find(
                (item) => item.seriesId === seriesId
              )
              return (
                <Field key={seriesId} orientation="horizontal">
                  <input
                    id={`${id}-${seriesId}`}
                    type="checkbox"
                    checked={selectedIds.includes(seriesId)}
                    onChange={(event) =>
                      onChange({
                        ...selection,
                        seriesIds: event.target.checked
                          ? [...selectedIds, seriesId]
                          : selectedIds.filter((value) => value !== seriesId),
                      })
                    }
                  />
                  <FieldLabel
                    htmlFor={`${id}-${seriesId}`}
                    className="min-w-0 break-words"
                  >
                    {schedule
                      ? scheduleName(schedule)
                      : `Unavailable schedule (${seriesId})`}
                  </FieldLabel>
                </Field>
              )
            })}
            <Field orientation="horizontal">
              <input
                id={`${id}-manual`}
                type="checkbox"
                checked={selection.includeManual}
                onChange={(event) =>
                  onChange({
                    ...selection,
                    includeManual: event.target.checked,
                  })
                }
              />
              <FieldLabel htmlFor={`${id}-manual`}>
                Include unscheduled runs
              </FieldLabel>
            </Field>
          </FieldGroup>
        </FieldSet>
      </details>
    </div>
  )
}
