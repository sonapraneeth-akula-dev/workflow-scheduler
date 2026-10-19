import { memo, useId } from "react"
import { CircleDashedIcon, RotateCcwIcon } from "lucide-react"
import { StatusBadge } from "@/components/StatusBadge"
import { executionStatuses } from "@/lib/live/runApi"
import type { GraphPosition } from "./layout"

export function NodePolicyIcons({
  optional,
  retries,
}: {
  optional: boolean
  retries: string | null
}) {
  return (
    <span
      aria-hidden="true"
      className="inline-flex shrink-0 items-center gap-1"
    >
      {optional ? (
        <span title="Optional node: soft-failure policy; still runs.">
          <CircleDashedIcon className="size-3.5" focusable="false" />
        </span>
      ) : null}
      {retries ? (
        <span title={retries}>
          <RotateCcwIcon className="size-3.5" focusable="false" />
        </span>
      ) : null}
    </span>
  )
}

export const GraphNode = memo(function GraphNode({
  position,
  status,
  optional,
  retries,
  taskCount,
  mode,
  selected,
  tabIndex,
  onFocus,
  onSelect,
}: {
  position: GraphPosition
  status: number
  optional: boolean
  retries: string | null
  taskCount: number
  mode: "Series" | "Parallel"
  selected: boolean
  tabIndex: number
  onFocus: (key: string) => void
  onSelect: (key: string) => void
}) {
  const descriptionId = useId()
  const label = executionStatuses[status]
  if (label === undefined) throw new Error("Unknown graph execution status.")
  const description = `${taskCount} ${taskCount === 1 ? "task" : "tasks"}, ${mode === "Series" ? "sequential" : "parallel"}. ${optional ? "Optional node: soft-failure policy; still runs." : "Required node: hard-failure policy."} ${retries ?? "No retries configured."}`
  return (
    <button
      type="button"
      data-graph-node={position.key}
      data-state={status}
      aria-label={`${position.key}: ${label}`}
      aria-describedby={descriptionId}
      aria-pressed={selected}
      title={`${position.key}: ${label}. ${description}`}
      tabIndex={tabIndex}
      style={position.style}
      className={`absolute flex flex-col justify-center gap-1 rounded-md border bg-card px-3 text-left text-card-foreground [contain:layout_paint] focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none ${selected ? "ring-2 ring-ring" : ""}`}
      onFocus={() => onFocus(position.key)}
      onClick={() => onSelect(position.key)}
    >
      <span className="flex w-full min-w-0 items-center gap-1">
        <span className="min-w-0 flex-1 truncate font-medium">
          {position.key}
        </span>
        <NodePolicyIcons optional={optional} retries={retries} />
      </span>
      <span className="flex w-full items-center justify-between gap-1">
        <StatusBadge status={label} />
        <span className="text-[10px] whitespace-nowrap text-muted-foreground">
          {taskCount} {taskCount === 1 ? "task" : "tasks"}
        </span>
      </span>
      <span id={descriptionId} className="sr-only">
        {description}
      </span>
    </button>
  )
})
