import { createFileRoute } from "@tanstack/react-router"
import { DeploymentsView } from "@/features/deployments/DeploymentsView"

export const Route = createFileRoute("/admin/deployments")({
  component: DeploymentsPage,
})

function DeploymentsPage() {
  return (
    <>
      <h1 className="text-2xl font-semibold">Deployments</h1>
      <DeploymentsView />
    </>
  )
}
