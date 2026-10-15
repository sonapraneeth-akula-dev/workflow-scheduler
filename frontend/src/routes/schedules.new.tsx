import { createFileRoute, useNavigate } from "@tanstack/react-router"
import { z } from "zod"
import { ScheduleForm } from "@/features/schedules/ScheduleForm"

export const Route = createFileRoute("/schedules/new")({
  validateSearch: z.object({ workflow: z.string().optional() }),
  component: NewSchedule,
})

function NewSchedule() {
  const { workflow } = Route.useSearch()
  const navigate = useNavigate()
  return (
    <>
      <h1 className="text-2xl font-semibold">Create schedule</h1>
      <ScheduleForm
        workflowId={workflow}
        onSaved={(snapshot) => {
          void navigate({
            to: "/schedules/$id",
            params: { id: snapshot.schedule.id },
          })
        }}
      />
    </>
  )
}
