import { describe, expect, it } from "vitest"
import { formatTimestamp } from "./timestamps"

describe("local timestamp display", () => {
  it("shows IST with three milliseconds without rounding the stored instant", () => {
    const original = "2026-10-16T08:00:03.1234567Z"
    expect(formatTimestamp(original, "Asia/Kolkata")).toBe(
      "16 Oct 2026, 13:30:03.123 IST"
    )
    expect(original).toBe("2026-10-16T08:00:03.1234567Z")
  })

  it.each([
    ["2026-01-16T08:09:10.007Z", "16 Jan 2026, 00:09:10.007 PST"],
    ["2026-07-16T08:09:10.000Z", "16 Jul 2026, 01:09:10.000 PDT"],
    ["2026-11-01T08:30:00.123Z", "01 Nov 2026, 01:30:00.123 PDT"],
    ["2026-11-01T09:30:00.123Z", "01 Nov 2026, 01:30:00.123 PST"],
  ])(
    "uses the timezone at the instant, including repeated DST hours: %s",
    (at, expected) => {
      expect(formatTimestamp(at, "America/Los_Angeles")).toBe(expected)
    }
  )

  it("handles local date rollover and explicit input offsets", () => {
    expect(formatTimestamp("2026-10-16T23:59:59.999999Z", "Asia/Kolkata")).toBe(
      "17 Oct 2026, 05:29:59.999 IST"
    )
    expect(
      formatTimestamp("2026-10-17T05:29:59.999999+05:30", "Asia/Kolkata")
    ).toBe("17 Oct 2026, 05:29:59.999 IST")
  })

  it("pads exact seconds with milliseconds and labels UTC", () => {
    expect(formatTimestamp("2026-10-16T00:00:00Z", "Etc/UTC")).toBe(
      "16 Oct 2026, 00:00:00.000 UTC"
    )
  })

  it("uses a named timezone when no English short abbreviation exists", () => {
    expect(
      formatTimestamp("2026-10-16T08:00:03.123Z", "Asia/Kathmandu")
    ).toMatch(/^16 Oct 2026, 13:45:03\.123 (?:Nepal Time|Asia\/Kathmandu)$/)
  })

  it("defaults to the browser's local timezone", () => {
    const at = "2026-10-16T08:00:03.123456Z"
    expect(formatTimestamp(at)).toBe(
      formatTimestamp(at, Intl.DateTimeFormat().resolvedOptions().timeZone)
    )
  })

  it.each(["invalid", "2026-10-16", "2026-10-16T08:00:03"])(
    "rejects invalid or timezone-less instants instead of inventing a timezone: %s",
    (at) => {
      expect(() => formatTimestamp(at)).toThrow(
        "Expected a timestamp with an explicit UTC offset."
      )
    }
  )
})
