import Papa from "papaparse"
import { z } from "zod"
import type { TableData } from "./types"

const scalar = z.union([z.string(), z.number().finite(), z.boolean(), z.null()])
const jsonTable = z.union([
  z.strictObject({
    columns: z.array(z.string()).min(1).max(128),
    rows: z.array(z.array(scalar)),
  }),
  z.array(
    z.custom<Record<string, z.infer<typeof scalar>>>(
      (value) =>
        typeof value === "object" &&
        value !== null &&
        !Array.isArray(value) &&
        Object.values(value).every((cell) => scalar.safeParse(cell).success)
    )
  ),
])

export function parseTable(text: string, format: "auto" | "csv"): TableData {
  let data: TableData
  if (format === "auto" && /^[\s]*[[{]/.test(text)) {
    let value: unknown
    try {
      value = JSON.parse(text)
    } catch {
      throw new Error("Table report is not valid JSON.")
    }
    const result = jsonTable.safeParse(value)
    if (!result.success)
      throw new Error(
        "Table JSON requires scalar rows and at most 128 columns."
      )
    if (Array.isArray(result.data)) {
      const columns = [
        ...new Set(result.data.flatMap((row) => Object.keys(row))),
      ]
      if (result.data.length && !columns.length)
        throw new Error("Table JSON rows have no columns.")
      data = {
        columns,
        rows: result.data.map((row) =>
          columns.map((key) =>
            Object.hasOwn(row, key) && row[key] !== null ? String(row[key]) : ""
          )
        ),
      }
    } else
      data = {
        columns: result.data.columns,
        rows: result.data.rows.map((row) =>
          row.map((cell) => (cell === null ? "" : String(cell)))
        ),
      }
  } else {
    const result = Papa.parse<string[]>(text, {
      delimiter: ",",
      skipEmptyLines: true,
      dynamicTyping: false,
    })
    if (result.errors.length)
      throw new Error(`Invalid CSV report: ${result.errors[0]!.code}.`)
    const [columns = [], ...rows] = result.data
    data = { columns, rows }
  }
  if (
    data.columns.length > 128 ||
    data.rows.some((row) => row.length !== data.columns.length)
  )
    throw new Error(
      "Table rows must match the header width, with at most 128 columns."
    )
  return data
}
