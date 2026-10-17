import { createFileRoute } from "@tanstack/react-router"
import { AuditFilters, AuditTrail } from "@/features/audit/AuditTrail"
import { auditSearchSchema } from "@/features/audit/api"

export const Route = createFileRoute("/admin/audit")({
  validateSearch: auditSearchSchema,
  component: AuditPage,
})

function AuditPage() {
  const search = Route.useSearch()
  const navigate = Route.useNavigate()
  return (
    <>
      <h1 className="text-2xl font-semibold">Audit trail</h1>
      <AuditFilters
        key={JSON.stringify(search)}
        search={search}
        onChange={(next) => void navigate({ search: next })}
      />
      <AuditTrail search={search} />
    </>
  )
}
