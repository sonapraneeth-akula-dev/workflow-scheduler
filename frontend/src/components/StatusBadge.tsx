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
  return (
    <Badge
      variant="outline"
      className={
        status === "Failed" || status === "TimedOut"
          ? "text-destructive"
          : undefined
      }
    >
      <Icon aria-hidden="true" focusable="false" />
      {status === "NeverRun" ? "Never run" : status}
    </Badge>
  )
}
