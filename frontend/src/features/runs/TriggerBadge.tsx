import { Badge } from "@/components/ui/badge"

export function TriggerBadge({
  trigger,
}: {
  trigger: "Manual" | "Schedule" | "CatchUp"
}) {
  const labels = {
    Manual: "Manual",
    Schedule: "Scheduled",
    CatchUp: "Catch-up",
  }
  return (
    <Badge
      variant="outline"
      title={
        trigger === "CatchUp"
          ? "Recovery run for the latest missed scheduled fire"
          : undefined
      }
    >
      {labels[trigger]}
    </Badge>
  )
}
