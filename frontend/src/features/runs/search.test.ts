import { describe, expect, it } from "vitest"
import { instantMicros, runSearchSchema } from "./search"

describe("typed run search URL", () => {
  it("round-trips every filter and grouping without losing exact metadata values", () => {
    const filters = {
      team: "team",
      type: "release",
      workflowId: "team/release/build",
      seriesId: "019ed132-2f44-7000-8000-000000000001",
      status: "Aborted",
      from: "2026-10-16T08:00:00.000001+01:00",
      to: "2026-10-16T07:00:00.000002Z",
      metadata: {
        environment: "prod",
        empty: "",
        exact: " leading & = space ",
      },
      groupBy: "metadata",
      groupKey: "component",
    }
    expect(runSearchSchema.parse(JSON.parse(JSON.stringify(filters)))).toEqual(
      filters
    )
    expect(instantMicros(filters.to) - instantMicros(filters.from)).toBe(1n)
  })

  it.each([
    { type: "release" },
    { team: "../foreign" },
    { workflowId: "team/release/build", team: "other" },
    { workflowId: "team/release/build", team: "team", type: "other" },
    { workflowId: "team/release" },
    { seriesId: "not-a-uuid" },
    { status: "running" },
    { status: 1 },
    { from: "2026-10-16T07:00:00" },
    { from: "2026-10-16T07:00:00.0000001Z" },
    { from: "2026-10-17T07:00:00Z", to: "2026-10-16T07:00:00Z" },
    { from: "2026-10-16T07:00:00.000002Z", to: "2026-10-16T07:00:00.000001Z" },
    { from: "2026-10-16T07:00:00Z", to: "2026-10-16T07:00:00Z" },
    { metadata: { environment: 1 } },
    { metadata: { "": "prod" } },
    {
      metadata: Object.fromEntries(
        Array.from({ length: 33 }, (_, i) => [`k${i}`, "v"])
      ),
    },
    { metadata: { environment: "x".repeat(1025) } },
    { groupBy: "metadata" },
    { groupBy: "unknown" },
  ])(
    "rejects malformed or contradictory filters before querying: %o",
    (filters) => {
      expect(runSearchSchema.safeParse(filters).success).toBe(false)
    }
  )

  it("retains own-property metadata names without treating prototype members as data", () => {
    const filters = runSearchSchema.parse(
      JSON.parse('{"metadata":{"__proto__":"value","constructor":"exact"}}')
    )
    expect(Object.hasOwn(filters.metadata!, "__proto__")).toBe(true)
    expect(filters.metadata?.constructor).toBe("exact")
    expect({}).not.toHaveProperty("value")
  })
})
