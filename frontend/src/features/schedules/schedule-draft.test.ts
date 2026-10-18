import { describe, expect, it } from "vitest"
import { formatTimestamp } from "@/lib/timestamps"
import {
  draftFor,
  durationSeconds,
  validateDraft,
  type ScheduleDraft,
} from "./schedule-draft"

const draft: ScheduleDraft = {
  ...draftFor(),
  workflowId: "team/release/workflow",
  name: "Daily",
}

describe("schedule draft boundaries", () => {
  it("uses shared API defaults and emits only the selected expression", () => {
    const result = validateDraft(draft)
    expect(result.errors.size).toBe(0)
    expect(result.request).toMatchObject({
      workflowId: "team/release/workflow",
      name: "Daily",
      timezone: "Etc/UTC",
      cron: "0 9 * * 1-5",
      interval: null,
      versionPin: null,
      variables: {},
      overlap: "Skip",
      catchUp: "Latest",
    })
  })

  it.each([1, 60, 86_399, 86_400, 2_147_483_647])(
    "round-trips %i whole seconds losslessly",
    (seconds) => {
      const result = validateDraft({
        ...draft,
        expression: "interval",
        seconds: String(seconds),
      })
      expect(result.errors.size).toBe(0)
      expect(result.request?.cron).toBeNull()
      expect(durationSeconds(result.request?.interval ?? "")).toBe(
        String(seconds)
      )
    }
  )

  it.each([
    "",
    "0",
    "-1",
    "1.5",
    "1e3",
    "Infinity",
    "2147483648",
    "9007199254740992",
  ])("rejects invalid interval %j", (seconds) => {
    expect(
      validateDraft({ ...draft, expression: "interval", seconds }).errors.has(
        "seconds"
      )
    ).toBe(true)
  })

  it.each([
    "",
    "team/type",
    "team/type/name/extra",
    "team/../name",
    "team/type/.",
    "team\\other/type/name",
    "team/type/\t",
    `${"a".repeat(129)}/type/name`,
  ])("rejects invalid workflow identity %j", (workflowId) => {
    expect(
      validateDraft({ ...draft, workflowId }).errors.has("workflowId")
    ).toBe(true)
  })

  it("counts Unicode code points rather than UTF-16 units", () => {
    expect(
      validateDraft({ ...draft, name: "😀".repeat(128) }).errors.size
    ).toBe(0)
    expect(
      validateDraft({ ...draft, name: "😀".repeat(129) }).errors.has("name")
    ).toBe(true)
    expect(
      validateDraft({ ...draft, workflowId: `${"😀".repeat(128)}/type/name` })
        .errors.size
    ).toBe(0)
  })

  it.each(["", "  ", "line\nbreak"])(
    "rejects empty/control-character schedule names %j",
    (name) => {
      expect(validateDraft({ ...draft, name }).errors.has("name")).toBe(true)
    }
  )

  it.each(["UTC", "Eastern Standard Time", "Mars/Olympus", "Not/A_Zone"])(
    "requires IANA timezone %j",
    (timezone) => {
      expect(validateDraft({ ...draft, timezone }).errors.has("timezone")).toBe(
        true
      )
    }
  )

  it.each([
    "America/New_York",
    "Europe/London",
    "Australia/Sydney",
    "Etc/GMT+3",
    "US/Eastern",
  ])("preserves valid IANA aliases %s", (timezone) => {
    expect(validateDraft({ ...draft, timezone }).request?.timezone).toBe(
      timezone
    )
  })

  it.each(["", "* * * *", "* * * * * *"])(
    "rejects non-five-field cron %j",
    (cron) => {
      expect(validateDraft({ ...draft, cron }).errors.has("cron")).toBe(true)
    }
  )

  it.each(["null", "[]", "2", '"value"', "{broken"])(
    "requires a JSON variable object %s",
    (variables) => {
      expect(
        validateDraft({ ...draft, variables }).errors.has("variables")
      ).toBe(true)
    }
  )

  it("preserves all JSON values and exact nonempty pins for server validation", () => {
    const result = validateDraft({
      ...draft,
      versionPin: " >=1.2.0 <2.0.0 ",
      variables:
        '{"flag":true,"none":null,"list":[1,"x"],"nested":{"key":"value"}}',
    })
    expect(result.request?.versionPin).toBe(">=1.2.0 <2.0.0")
    expect(result.request?.variables).toEqual({
      flag: true,
      none: null,
      list: [1, "x"],
      nested: { key: "value" },
    })
  })

  it("enforces the serialized request's exact UTF-8 byte limit", () => {
    const base = validateDraft({
      ...draft,
      variables: '{"payload":""}',
    }).request
    expect(base).toBeDefined()
    const overhead = new TextEncoder().encode(JSON.stringify(base)).length
    const exact = {
      ...draft,
      variables: JSON.stringify({ payload: "x".repeat(1_048_576 - overhead) }),
    }
    expect(validateDraft(exact).request).toBeDefined()
    const oversized = {
      ...exact,
      variables: JSON.stringify({ payload: "x".repeat(1_048_577 - overhead) }),
    }
    expect(validateDraft(oversized).errors.get("variables")).toContain("1 MiB")
    const unicode = {
      ...draft,
      variables: JSON.stringify({ payload: "😀".repeat(262_144) }),
    }
    expect(validateDraft(unicode).request).toBeUndefined()
  })

  it("shows distinct timezone labels for the repeated local DST hour", () => {
    const first = formatTimestamp("2026-11-01T05:30:00Z", "America/New_York")
    const second = formatTimestamp("2026-11-01T06:30:00Z", "America/New_York")
    expect(first).toContain("01:30:00.000 EDT")
    expect(second).toContain("01:30:00.000 EST")
    expect(first).not.toBe(second)
  })

  it("never rounds an Int64 variable override while validating the draft", () => {
    const result = validateDraft({
      ...draft,
      variables: '{"sequence":9223372036854775807}',
    })
    expect(result.request).toBeDefined()
    expect(JSON.stringify(result.request?.variables)).toBe(
      '{"sequence":9223372036854775807}'
    )
  })

  it("fails explicitly on malformed persisted intervals", () => {
    expect(() => durationSeconds("not-an-interval")).toThrow(
      "unsupported schedule interval"
    )
    expect(() => durationSeconds("00:99:00")).toThrow(
      "unsupported schedule interval"
    )
    expect(() => durationSeconds("00:00:00")).toThrow(
      "out-of-range schedule interval"
    )
    expect(() => durationSeconds("24856.00:00:00")).toThrow(
      "out-of-range schedule interval"
    )
  })
})
