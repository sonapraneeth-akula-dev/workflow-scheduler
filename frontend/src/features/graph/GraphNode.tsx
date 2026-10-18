import { memo } from "react"
import { StatusBadge } from "@/components/StatusBadge"
import { executionStatuses } from "@/lib/live/runApi"
import type { GraphPosition } from "./layout"

export const GraphNode = memo(function GraphNode({
  position,
  status,
  selected,
  tabIndex,
  onFocus,
  onSelect,
}: {
  position: GraphPosition
  status: number
  selected: boolean
  tabIndex: number
  onFocus: (key: string) => void
  onSelect: (key: string) => void
}) {
  const label = executionStatuses[status]
  if (label === undefined) throw new Error("Unknown graph execution status.")
  return (
    <button
      type="button"
      data-graph-node={position.key}
      data-state={status}
      aria-label={`${position.key}: ${label}`}
      aria-pressed={selected}
      title={`${position.key}: ${label}`}
      tabIndex={tabIndex}
      style={position.style}
      className={`absolute flex flex-col justify-center gap-1 rounded-md border bg-card px-3 text-left text-card-foreground [contain:layout_paint] focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none ${selected ? "ring-2 ring-ring" : ""}`}
      onFocus={() => onFocus(position.key)}
      onClick={() => onSelect(position.key)}
    >
      <span className="w-full truncate font-medium">{position.key}</span>
      <StatusBadge status={label} />
    </button>
  )
})
