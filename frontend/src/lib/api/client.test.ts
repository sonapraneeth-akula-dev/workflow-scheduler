import { describe, expect, it } from "vitest"
import { ApiError, scheduleSchema } from "./client"

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
