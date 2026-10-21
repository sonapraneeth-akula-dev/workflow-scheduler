import { createFileRoute } from "@tanstack/react-router"
import { SystemView } from "@/features/system/SystemView"

export const Route = createFileRoute("/admin/system")({
  component: SystemPage,
})

function SystemPage() {
  return (
    <>
      <h1 className="text-2xl font-semibold">Live system</h1>
      <SystemView />
    </>
  )
}
