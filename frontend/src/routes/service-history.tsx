import { createFileRoute } from "@tanstack/react-router"
import { TeamList } from "@/features/catalog/TeamList"

export const Route = createFileRoute("/service-history")({
  component: ServiceTeams,
})

function ServiceTeams() {
  return (
    <>
      <h1 className="text-2xl font-semibold">Service history</h1>
      <p>Choose a team to explore workflow health and run history.</p>
      <TeamList destination="services" />
    </>
  )
}
