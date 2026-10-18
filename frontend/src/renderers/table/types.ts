export interface TableData {
  columns: string[]
  rows: string[][]
}

export interface TablePage {
  page: number
  pageCount: number
  parsedRows: number
  complete: boolean
}
export type TableResult =
  { data: TableData; pagination?: TablePage } | { error: string }
export type TableRequest =
  { content: Blob; format: "auto" | "csv" } | { page: number }
