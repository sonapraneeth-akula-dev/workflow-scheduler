import Papa from "papaparse"
import type { TableData } from "./types"

type Scalar = string | number | boolean | null
const isScalar = (value: unknown): value is Scalar =>
  value === null ||
  typeof value === "string" ||
  typeof value === "boolean" ||
  (typeof value === "number" && Number.isFinite(value))
const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value)
const isScalarRecord = (value: unknown): value is Record<string, Scalar> =>
  isRecord(value) && Object.values(value).every(isScalar)
function isExplicitTable(value: unknown): value is {
  columns: string[]
  rows: Scalar[][]
} {
  return (
    isRecord(value) &&
    Object.keys(value).length === 2 &&
    Object.hasOwn(value, "columns") &&
    Object.hasOwn(value, "rows") &&
    Array.isArray(value.columns) &&
    value.columns.length >= 1 &&
    value.columns.length <= 128 &&
    value.columns.every((cell) => typeof cell === "string") &&
    Array.isArray(value.rows) &&
    value.rows.every(
      (row: unknown) => Array.isArray(row) && row.every(isScalar)
    )
  )
}

export function parseTable(text: string, format: "auto" | "csv"): TableData {
  let data: TableData
  if (format === "auto" && /^[\s]*[[{]/.test(text)) {
    let value: unknown
    try {
      value = JSON.parse(text)
    } catch {
      throw new Error("Table report is not valid JSON.")
    }
    if (Array.isArray(value) && value.every(isScalarRecord)) {
      const columns = [...new Set(value.flatMap((row) => Object.keys(row)))]
      if (value.length && !columns.length)
        throw new Error("Table JSON rows have no columns.")
      data = {
        columns,
        rows: value.map((row) =>
          columns.map((key) =>
            Object.hasOwn(row, key) && row[key] !== null ? String(row[key]) : ""
          )
        ),
      }
    } else if (isExplicitTable(value))
      data = {
        columns: value.columns,
        rows: value.rows.map((row) =>
          row.map((cell) => (cell === null ? "" : String(cell)))
        ),
      }
    else
      throw new Error(
        "Table JSON requires scalar rows and at most 128 columns."
      )
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
  return validateTable(data)
}

export function validateTable(data: TableData): TableData {
  if (
    data.columns.length > 128 ||
    data.rows.some((row) => row.length !== data.columns.length)
  )
    throw new Error(
      "Table rows must match the header width, with at most 128 columns."
    )
  return data
}
