import { createFileRoute } from "@tanstack/react-router"
import { RecurringJobs } from "@/features/schedules/RecurringJobs"
import { recurringJobsSearchSchema } from "@/features/schedules/recurring-jobs-api"

export const Route = createFileRoute("/recurring-jobs")({
  validateSearch: recurringJobsSearchSchema,
  component: RecurringJobsPage,
})

function RecurringJobsPage() {
  const search = Route.useSearch()
  const navigate = Route.useNavigate()
  return (
    <RecurringJobs
      search={search}
      onChange={(next) => void navigate({ search: next })}
    />
  )
}
