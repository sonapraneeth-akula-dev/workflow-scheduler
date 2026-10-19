import { createFileRoute, Link } from "@tanstack/react-router"
import { ServiceHistory } from "@/features/services/ServiceHistory"
import { serviceSearchSchema } from "@/features/services/api"
import {
  presentationName,
  usePresentation,
} from "@/features/catalog/usePresentation"

export const Route = createFileRoute("/t/$team/services")({
  validateSearch: serviceSearchSchema,
  component: TeamServices,
})

function TeamServices() {
  const { team } = Route.useParams()
  const search = Route.useSearch()
  const presentation = usePresentation(team)
  const navigate = Route.useNavigate()
  return (
    <>
      <nav aria-label="Breadcrumb" className="flex flex-wrap gap-2">
        <Link to="/service-history">Service history</Link>
        <span aria-hidden="true">/</span>
        <Link to="/t/$team" params={{ team }}>
          {presentationName(presentation.data ?? null, team)}
        </Link>
      </nav>
      <ServiceHistory
        team={team}
        search={search}
        onChange={(next) => void navigate({ search: next })}
      />
    </>
  )
}
