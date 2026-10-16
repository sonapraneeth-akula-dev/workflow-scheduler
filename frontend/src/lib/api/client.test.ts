import { describe, expect, it } from "vitest"
import {
  ApiError,
  permissionResourceSchema,
  permissionsSchema,
  scheduleSchema,
  seriesFiresSchema,
} from "./client"

describe("caller permission trust boundaries", () => {
  const resource = { scope: "type", id: "team/release" }
  it("requires an explicit resource and booleans for every role", () => {
    const valid = {
      ...resource,
      viewer: true,
      author: false,
      operator: false,
      admin: false,
    }
    expect(permissionsSchema.parse(valid)).toEqual(valid)
    for (const body of [
      resource,
      { ...valid, operator: "true" },
      { ...valid, viewer: null },
      { ...valid, scope: "Type" },
    ]) {
      expect(permissionsSchema.safeParse(body).success).toBe(false)
    }
  })
  it.each(["", "\n", "bad\u0000resource", "a".repeat(1025)])(
    "rejects invalid permission resource %j",
    (id) => {
      expect(
        permissionResourceSchema.safeParse({ ...resource, id }).success
      ).toBe(false)
    }
  )
})

describe("schedule API trust boundaries", () => {
  it("preserves stale-edit recovery instructions and status", () => {
    const error = new ApiError(new Response(null, { status: 412 }), {
      title: "Changed",
    })

    expect(error.status).toBe(412)
    expect(error.message).toContain("Your edits are preserved")
    expect(error.message).toContain("Reload")
  })

  it("surfaces validation paths and safe hints", () => {
    const error = new ApiError(new Response(null, { status: 400 }), {
      title: "Schedule validation failed",
      errors: [
        { path: "$.cron", message: "Invalid cron", hint: "Use five fields." },
      ],
    })
    expect(error.fields.get("$.cron")).toBe("Use five fields.")
    expect(error.message).toContain("Use five fields.")
  })

  it("surfaces non-Problem-Details errors rather than success-shaped fallbacks", () => {
    const error = new ApiError(
      new Response(null, { status: 502 }),
      "<html>gateway failure</html>"
    )
    expect(error.message).toContain("HTTP 502")
    expect(error.fields.size).toBe(0)
  })

  it.each([{ id: "invalid" }, null, { rowVersion: 0 }, { origin: "Unknown" }])(
    "rejects malformed schedule responses",
    (response) => {
      expect(scheduleSchema.safeParse(response).success).toBe(false)
    }
  )
})

describe("series fire history trust boundaries", () => {
  const page = {
    seriesId: "a77bb9c1-eebc-4145-a63b-296d62ee8747",
    items: [
      {
        fireAt: "2026-10-15T12:00:00.000001Z",
        outcome: "Skipped",
        runId: null,
        reason: "overlap",
      },
    ],
    nextCursor: null,
  }
  it("preserves exact timestamps, skip reason and absent run", () => {
    expect(seriesFiresSchema.parse(page)).toEqual(page)
  })
  it.each([
    { ...page, seriesId: "not-a-series" },
    { ...page, nextCursor: "invalid cursor" },
    { ...page, items: [{ ...page.items[0], outcome: "unknown" }] },
    { ...page, items: [{ ...page.items[0], fireAt: "yesterday" }] },
    { ...page, items: [{ ...page.items[0], runId: "not-a-run" }] },
    { ...page, items: Array.from({ length: 101 }, () => page.items[0]) },
  ])("rejects malformed or unbounded fire history", (body) => {
    expect(seriesFiresSchema.safeParse(body).success).toBe(false)
  })
})
