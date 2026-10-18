import { createFileRoute } from "@tanstack/react-router"
import { TeamArea } from "@/features/catalog/TeamArea"

export const Route = createFileRoute("/t/$team/")({ component: TeamPage })

function TeamPage() {
  const { team } = Route.useParams()
  return <TeamArea team={team} />
}
