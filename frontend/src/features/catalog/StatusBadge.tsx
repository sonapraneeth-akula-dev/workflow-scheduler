import { Badge } from "@/components/ui/badge"
import type { RunStatus } from "./api"

export function StatusBadge({ status }: { status: RunStatus }) {
  return (
    <Badge
      variant="outline"
      className={
        status === "Failed" || status === "TimedOut"
          ? "text-destructive"
          : undefined
      }
    >
      {status === "NeverRun" ? "Never run" : status}
    </Badge>
  )
}
