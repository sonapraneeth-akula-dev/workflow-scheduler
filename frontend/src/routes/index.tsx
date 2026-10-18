import { createFileRoute, Link } from "@tanstack/react-router"
import { TeamList } from "@/features/catalog/TeamList"

export const Route = createFileRoute("/")({
  component: () => (
    <>
      <h1 className="text-2xl font-semibold">Workflow Scheduler</h1>
      <p>
        Browse permitted teams and workflows, or manage recurring schedules.
      </p>
      <TeamList />
      <Link to="/schedules/new">Create schedule</Link>
    </>
  ),
})
