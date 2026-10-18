import { createFileRoute, Outlet } from "@tanstack/react-router"

export const Route = createFileRoute("/t/$team/$type/w/$wf/versions")({
  component: Outlet,
})
