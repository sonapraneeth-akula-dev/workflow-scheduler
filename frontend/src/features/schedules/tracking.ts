import { z } from "zod"
import { presentationName } from "@/features/catalog/usePresentation"

const seriesId = z
  .uuid()
  .refine(
    (value) =>
      value === value.toLowerCase() &&
      value !== "00000000-0000-0000-0000-000000000000"
  )
const selectedSeries = z
  .array(seriesId)
  .max(32)
  .refine((ids) => new Set(ids).size === ids.length)
export const trackingSchema = z.object({
  seriesIds: selectedSeries.nullable(),
  includeManual: z.boolean(),
})
export type ScheduleTrackingSelection = z.infer<typeof trackingSchema>
export const defaultTracking: ScheduleTrackingSelection = {
  seriesIds: null,
  includeManual: true,
}
export const seriesSelectionSchema = z
  .string()
  .refine(
    (value) =>
      value === "none" || selectedSeries.safeParse(value.split(",")).success,
    "Select at most 32 distinct schedule series."
  )
export const trackedScheduleSchema = z.object({
  seriesId,
  scheduleId: z.uuid(),
  name: z.string().trim().min(1),
  paused: z.boolean(),
  deleted: z.boolean(),
})
export type TrackedSchedule = z.infer<typeof trackedScheduleSchema>

export function trackingSearch(selection: ScheduleTrackingSelection) {
  return {
    seriesIds:
      selection.seriesIds === null
        ? undefined
        : selection.seriesIds.toSorted().join(",") || "none",
    includeManual: selection.includeManual ? undefined : ("false" as const),
  }
}

export function scheduleName(schedule: TrackedSchedule) {
  return `${presentationName(null, schedule.name)}${schedule.deleted ? " (deleted)" : schedule.paused ? " (paused)" : ""}`
}

export function trackingDescription(
  schedules: readonly TrackedSchedule[],
  selection: ScheduleTrackingSelection
) {
  const ids = selection.seriesIds ?? schedules.map((item) => item.seriesId)
  const labels = ids.map((id) => {
    const schedule = schedules.find((item) => item.seriesId === id)
    return schedule ? scheduleName(schedule) : `Unavailable schedule (${id})`
  })
  if (selection.seriesIds === null)
    labels.unshift("All schedules (including future)")
  if (selection.includeManual) labels.push("Unscheduled runs")
  return labels.join(", ") || "Nothing selected"
}
