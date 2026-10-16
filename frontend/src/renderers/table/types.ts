export interface TableData {
  columns: string[]
  rows: string[][]
}

export type TableResult = { data: TableData } | { error: string }
export interface TableRequest {
  content: Blob
  format: "auto" | "csv"
}
