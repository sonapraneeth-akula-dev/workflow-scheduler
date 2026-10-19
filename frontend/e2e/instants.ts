import { expect, type Locator } from "@playwright/test"

export function instantTicks(value: string): bigint {
  const fraction = /\.(\d+)/.exec(value)?.[1] ?? ""
  if (fraction.length > 7) throw new Error("Timestamp exceeds 100ns precision.")
  return (
    BigInt(Date.parse(value)) * 10_000n +
    BigInt(fraction.padEnd(7, "0").slice(3))
  )
}

export async function expectInstant(time: Locator, expected: string) {
  await expect(time).toHaveAttribute("datetime", /\S+/)
  const actual = await time.getAttribute("datetime")
  if (actual === null) throw new Error("Recorded time has no datetime.")
  expect(instantTicks(actual)).toBe(instantTicks(expected))
  await expect(time).toHaveAttribute("title", actual)
}
