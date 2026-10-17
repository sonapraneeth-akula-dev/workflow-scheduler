import { describe, expect, it } from "vitest"
import { parseRunDelta } from "./messages"

const at = "2026-10-17T12:00:00.0000000+00:00"
const event = {
  seq: 1,
  at,
  entity: 2,
  key: "work/task",
  state: 3,
  abort: null,
  attempt: 1,
  progress: { sequence: 1, phase: 3, occurredAt: at },
}
const delta = (change: unknown) => ({
  runId: "019ed132-2f44-7000-8000-000000000001",
  events: [change],
})

describe("bounded remote operation System Events", () => {
  it("accepts every fixed phase and retains the per-attempt identity", () => {
    for (let phase = 1; phase <= 9; phase++) {
      const change = { ...event, progress: { ...event.progress, phase } }
      expect(parseRunDelta(delta(change)).events[0]).toEqual(change)
    }
  })

  it.each([
    { ...event, entity: 1 },
    { ...event, state: 6 },
    { ...event, key: null },
    { ...event, attempt: null },
    { ...event, attempt: 32768 },
    { ...event, waitingReason: "WaitingForMachine" },
    { ...event, agentId: "019ed132-2f44-7000-8000-000000000002" },
    { ...event, progress: { ...event.progress, sequence: 0 } },
    { ...event, progress: { ...event.progress, sequence: 65 } },
    { ...event, progress: { ...event.progress, phase: 0 } },
    { ...event, progress: { ...event.progress, phase: 10 } },
    { ...event, progress: { ...event.progress, occurredAt: "bad" } },
    {
      ...event,
      progress: { ...event.progress, url: "https://unsafe.invalid" },
    },
  ])("rejects malformed or freeform progress payload %#", (change) => {
    expect(() => parseRunDelta(delta(change))).toThrow()
  })
})
