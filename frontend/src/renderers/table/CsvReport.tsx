import type { ReportRendererProps } from "../types"
import { TableContent } from "./TableReport"

export default function CsvReport(props: ReportRendererProps) {
  return <TableContent {...props} format="csv" />
}
