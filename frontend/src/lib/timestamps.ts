const localTimeZone = Intl.DateTimeFormat().resolvedOptions().timeZone
const formatters = new Map<string, Intl.DateTimeFormat[]>()

export function formatTimestamp(
  value: string,
  timeZone = localTimeZone
): string {
  const date = new Date(value)
  if (
    !/(?:Z|[+-]\d{2}:\d{2})$/i.test(value) ||
    !Number.isFinite(date.getTime())
  ) {
    throw new RangeError("Expected a timestamp with an explicit UTC offset.")
  }
  let formats = formatters.get(timeZone)
  if (!formats) {
    formats = ["en-GB", "en-US", "en-IN", "en-GB"].map(
      (locale, index) =>
        new Intl.DateTimeFormat(locale, {
          timeZone,
          day: "2-digit",
          month: "short",
          year: "numeric",
          hour: "2-digit",
          minute: "2-digit",
          second: "2-digit",
          fractionalSecondDigits: 3,
          hourCycle: "h23",
          timeZoneName: index === 3 ? "long" : "short",
        })
    )
    formatters.set(timeZone, formats)
  }
  const parts = formats[0]!.formatToParts(date)
  let zone = timeZone
  // Short names depend on locale: IST in en-IN, PST/PDT in en-US.
  for (const formatter of formats) {
    const name = formatter
      .formatToParts(date)
      .find((part) => part.type === "timeZoneName")!.value
    if (!/^(?:GMT|UTC)[+-]/.test(name)) {
      zone = name
      break
    }
  }
  return parts
    .map((part) => (part.type === "timeZoneName" ? zone : part.value))
    .join("")
}
