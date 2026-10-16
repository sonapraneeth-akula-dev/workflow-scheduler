import { z } from "zod"

const traceSchema = z.object({
  traceEvents: z.array(
    z.object({
      name: z.string(),
      ph: z.string(),
      ts: z.number(),
      pid: z.number().int(),
      args: z.record(z.string(), z.unknown()).optional(),
    })
  ),
})
const ownerSchema = z.object({
  data: z.object({ frame: z.string(), layerTreeId: z.number().int() }),
})
const frameSchema = z.object({
  layerTreeId: z.number().int(),
  frameSeqId: z.number().int().nonnegative(),
})

export function analyzeGraphTrace(input: unknown, frameId: string) {
  const { traceEvents: events } = traceSchema.parse(input)
  const mark = (name: string) => {
    const matches = events.filter(
      (event) => event.name === name && (event.ph === "R" || event.ph === "I")
    )
    if (matches.length !== 1)
      throw new Error(`Chrome trace requires exactly one ${name} mark.`)
    return matches[0]!.ts
  }
  const start = mark("graph-load-start"),
    end = mark("graph-load-end")
  if (end - start < 1_000_000)
    throw new Error("Chrome graph load window is too short.")
  const owners = new Map<string, { pid: number; layerTreeId: number }>()
  for (const event of events.filter(
    (event) => event.name === "SetLayerTreeId"
  )) {
    const owner = ownerSchema.safeParse(event.args)
    if (owner.success && owner.data.data.frame === frameId)
      owners.set(`${event.pid}/${owner.data.data.layerTreeId}`, {
        pid: event.pid,
        layerTreeId: owner.data.data.layerTreeId,
      })
  }
  if (owners.size !== 1)
    throw new Error(
      "Chrome trace does not uniquely identify the measured page's layer tree."
    )
  const owner = [...owners.values()][0]!
  const frames = new Map<number, number>()
  let droppedFrames = 0
  for (const event of events) {
    if (
      event.pid !== owner.pid ||
      event.ts < start ||
      event.ts > end ||
      event.ph !== "I" ||
      (event.name !== "DrawFrame" && event.name !== "DroppedFrame")
    )
      continue
    const frame = frameSchema.parse(event.args)
    if (frame.layerTreeId !== owner.layerTreeId) continue
    if (event.name === "DroppedFrame") {
      droppedFrames++
      continue
    }
    const previous = frames.get(frame.frameSeqId)
    frames.set(
      frame.frameSeqId,
      previous === undefined ? event.ts : Math.min(previous, event.ts)
    )
  }
  const timestamps = [...frames.values()].sort((a, b) => a - b)
  if (timestamps.length < 2)
    throw new Error(
      "Chrome trace has insufficient actual page DrawFrame events."
    )
  const span = timestamps.at(-1)! - timestamps[0]!
  if (span <= 0) throw new Error("Chrome frame timestamps do not advance.")
  const intervals = timestamps
    .slice(1)
    .map((at, index) => (at - timestamps[index]!) / 1000)
    .sort((a, b) => a - b)
  return {
    source:
      "Chrome instant DrawFrame events, deduplicated by page process/layerTreeId/frameSeqId",
    frameId,
    ...owner,
    frameCount: timestamps.length,
    measuredSeconds: span / 1_000_000,
    fps: ((timestamps.length - 1) * 1_000_000) / span,
    p95FrameIntervalMs: intervals[Math.ceil(intervals.length * 0.95) - 1]!,
    maxFrameIntervalMs: intervals.at(-1)!,
    droppedFrames,
    loadWindowSeconds: (end - start) / 1_000_000,
  }
}
