import { createFileRoute } from "@tanstack/react-router"
import { DashboardPage } from "@/features/dashboards/DashboardPage"
import {
  dashboardResourceSchema,
  dashboardSearchSchema,
} from "@/features/dashboards/api"

export const Route = createFileRoute("/dashboards/$scope/$id")({
  params: { parse: (params) => dashboardResourceSchema.parse(params) },
  validateSearch: dashboardSearchSchema,
  component: DashboardRoute,
})

function DashboardRoute() {
  const resource = Route.useParams()
  const search = Route.useSearch()
  const navigate = Route.useNavigate()
  return (
    <DashboardPage
      {...resource}
      {...search}
      onChange={(next) => void navigate({ search: next })}
    />
  )
}
