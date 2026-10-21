import { createFileRoute } from "@tanstack/react-router"
import { SystemView } from "@/features/system/SystemView"
import { SystemNoticeEditor } from "@/features/notices/SystemNoticeEditor"

export const Route = createFileRoute("/admin/system")({
  component: SystemPage,
})

function SystemPage() {
  return (
    <>
      <h1 className="text-2xl font-semibold">Actual system</h1>
      <SystemNoticeEditor />
      <SystemView />
    </>
  )
}
