import { z } from "zod"

const chartSchema = z
  .strictObject({
    type: z.enum(["line", "bar"]),
    xLabel: z.string().trim().min(1).max(256).optional(),
    yLabel: z.string().trim().min(1).max(256).optional(),
    x: z.array(z.union([z.number().finite(), z.string()])).min(1),
    series: z
      .array(
        z.strictObject({
          label: z.string().min(1).max(128),
          values: z.array(z.number().finite().nullable()),
        })
      )
      .min(1)
      .max(16),
  })
  .refine(
    (chart) =>
      chart.series.every((series) => series.values.length === chart.x.length) &&
      (chart.type === "bar" ||
        chart.x.every((value, index) => {
          const previous = chart.x[index - 1]
          return (
            typeof value === "number" &&
            (index === 0 || (typeof previous === "number" && previous < value))
          )
        })),
    "Chart series must align with X values; line X values must increase."
  )

export function parseChart(text: string) {
  let value: unknown
  try {
    value = JSON.parse(text)
  } catch {
    throw new Error("Chart report is not valid JSON.")
  }
  const result = chartSchema.safeParse(value)
  if (!result.success)
    throw new Error(
      "Chart requires line/bar type, X values and 1-16 aligned numeric series. Line X values must strictly increase."
    )
  return result.data
}
export type ChartData = ReturnType<typeof parseChart>
