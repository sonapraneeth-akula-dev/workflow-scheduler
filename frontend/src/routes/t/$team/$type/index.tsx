import { createFileRoute } from "@tanstack/react-router"
import { TypeWorkflows } from "@/features/catalog/TypeWorkflows"

export const Route = createFileRoute("/t/$team/$type/")({ component: TypePage })

function TypePage() {
  const { team, type } = Route.useParams()
  return <TypeWorkflows team={team} type={type} />
}
