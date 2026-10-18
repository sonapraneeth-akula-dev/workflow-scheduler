import { describe, expect, it } from "vitest"
import { analyzeGraphTrace } from "../../../scripts/graph-trace"

const event = (name: string, ts: number, args = {}, pid = 7, ph = "I") => ({
  name,
  ts,
  args,
  pid,
  ph,
})
function trace(fps = 60) {
  return {
    traceEvents: [
      event("SetLayerTreeId", 0, { data: { frame: "page", layerTreeId: 4 } }),
      event("graph-load-start", 0, {}, 7, "R"),
      ...Array.from({ length: fps + 1 }, (_, index) =>
        event("DrawFrame", (index * 1_000_000) / fps, {
          layerTreeId: 4,
          frameSeqId: index,
        })
      ),
      event("graph-load-end", 1_000_000, {}, 7, "R"),
    ],
  }
}

describe("actual Chrome graph trace analysis", () => {
  it("uses closed-interval frame cadence, not callback counts or a one-frame boundary overcount", () => {
    const result = analyzeGraphTrace(trace(), "page")
    expect(result.frameCount).toBe(61)
    expect(result.fps).toBe(60)
    expect(result.measuredSeconds).toBe(1)
    expect(result.droppedFrames).toBe(0)
    expect(analyzeGraphTrace(trace(59), "page").fps).toBeLessThan(60)
  })

  it("cannot inflate FPS using another renderer/layer, duplicate frame IDs or old async DrawFrame events", () => {
    const data = trace()
    data.traceEvents.push(
      event("DrawFrame", 500_000, { layerTreeId: 4, frameSeqId: 30 }),
      event("DrawFrame", 500_000, { layerTreeId: 99, frameSeqId: 999 }),
      event("DrawFrame", 500_000, { layerTreeId: 4, frameSeqId: 999 }, 99),
      event("DrawFrame", 500_000, { layerTreeId: 4, frameSeqId: 999 }, 7, "b"),
      event("DrawFrame", 1_500_000, { layerTreeId: 4, frameSeqId: 1000 })
    )
    expect(analyzeGraphTrace(data, "page").fps).toBe(60)
  })

  it("reports matching dropped frames and exact long frame intervals", () => {
    const data = trace()
    data.traceEvents.push(
      event("DroppedFrame", 500_000, { layerTreeId: 4, frameSeqId: 999 })
    )
    data.traceEvents = data.traceEvents.filter(
      (item) => item.name !== "DrawFrame" || item.ts !== 500_000
    )
    const result = analyzeGraphTrace(data, "page")
    expect(result.droppedFrames).toBe(1)
    expect(result.fps).toBe(59)
    expect(result.maxFrameIntervalMs).toBeCloseTo(1000 / 30)
  })

  it.each([
    { traceEvents: [] },
    {
      traceEvents: trace().traceEvents.filter(
        (item) => item.name !== "SetLayerTreeId"
      ),
    },
    {
      traceEvents: trace().traceEvents.filter(
        (item) => item.name !== "DrawFrame"
      ),
    },
    {
      traceEvents: [...trace().traceEvents, event("graph-load-end", 1_000_000)],
    },
    {
      traceEvents: trace().traceEvents.map((item) =>
        item.name === "DrawFrame" ? { ...item, args: {} } : item
      ),
    },
  ])(
    "rejects absent, ambiguous or incomplete evidence instead of success-shaped FPS",
    (data) => {
      expect(() => analyzeGraphTrace(data, "page")).toThrow()
    }
  )
})
