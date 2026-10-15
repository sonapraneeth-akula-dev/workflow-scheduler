import { createFileRoute, Link } from "@tanstack/react-router"

export const Route = createFileRoute("/")({
  component: () => (
    <>
      <h1 className="text-2xl font-semibold">Workflow Scheduler</h1>
      <p>Manage recurring workflow schedules.</p>
      <Link to="/schedules/new">Create schedule</Link>
    </>
  ),
})
