import {
  AlarmClock,
  Ban,
  Circle,
  CircleAlert,
  CircleCheck,
  CircleDot,
  CircleX,
  Clock,
  Copy,
  OctagonPause,
  Play,
  RotateCw,
  Send,
  SkipForward,
  type LucideIcon,
} from "lucide-react"
import { Badge } from "@/components/ui/badge"
import type { RunStatus } from "@/features/catalog/api"
import type { executionStatuses } from "@/lib/live/runApi"

type Status = RunStatus | (typeof executionStatuses)[number]
const icons = {
  Pending: Clock,
  Ready: CircleDot,
  Dispatched: Send,
  Running: Play,
  Retrying: RotateCw,
  Succeeded: CircleCheck,
  SoftFailed: CircleAlert,
  Failed: CircleX,
  TimedOut: AlarmClock,
  Cancelled: Ban,
  Skipped: SkipForward,
  Reused: Copy,
  Queued: Clock,
  Aborting: OctagonPause,
  Aborted: Ban,
  NeverRun: Circle,
} satisfies Record<Status, LucideIcon>

export function StatusBadge({ status }: { status: Status }) {
  const Icon = icons[status]
  const color =
    status === "Succeeded"
      ? "border-success/35 bg-success/10 text-success"
      : status === "Failed"
        ? "border-destructive/35 bg-destructive/10 text-destructive"
        : status === "TimedOut"
          ? "border-chart-4/35 bg-chart-4/10 text-chart-4"
          : ["SoftFailed", "Retrying", "Aborting"].includes(status)
            ? "border-warning/35 bg-warning/10 text-warning"
            : ["Ready", "Dispatched", "Running", "Reused", "Queued"].includes(
                  status
                )
              ? "border-info/35 bg-info/10 text-info"
              : "bg-muted text-muted-foreground"
  return (
    <Badge variant="outline" className={color}>
      <Icon aria-hidden="true" focusable="false" />
      {status === "NeverRun" ? "Never run" : status}
    </Badge>
  )
}
