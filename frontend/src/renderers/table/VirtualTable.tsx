import { useMemo, useState } from "react"
import { Button } from "@/components/ui/button"
import { NativeSelect } from "@/components/ui/native-select"
import type { TableData } from "./types"

const rowHeight = 32
const windowSize = 24
const collator = new Intl.Collator("en-US", { numeric: true })

export function VirtualTable({
  columns,
  rows,
  title,
}: TableData & { title: string }) {
  const [filter, setFilter] = useState("")
  const [sort, setSort] = useState(-1)
  const [descending, setDescending] = useState(false)
  const [scrollTop, setScrollTop] = useState(0)
  const filtered = useMemo(() => {
    const query = filter.toLocaleLowerCase("en-US")
    const selected = query
      ? rows.filter((row) =>
          row.some((cell) => cell.toLocaleLowerCase("en-US").includes(query))
        )
      : rows
    return sort < 0
      ? selected
      : selected.toSorted(
          (left, right) =>
            collator.compare(left[sort]!, right[sort]!) * (descending ? -1 : 1)
        )
  }, [rows, filter, sort, descending])
  const start = Math.min(
    Math.max(0, Math.floor((scrollTop - rowHeight) / rowHeight) - 4),
    Math.max(0, filtered.length - windowSize)
  )
  const end = Math.min(filtered.length, start + windowSize)
  return (
    <section aria-label={`${title} table`} className="min-w-0 space-y-3">
      <div className="flex flex-wrap items-end gap-3">
        <label className="flex min-w-0 flex-col gap-1">
          Filter table rows
          <input
            type="search"
            value={filter}
            className="h-9 w-full min-w-0 rounded-md border px-3"
            onChange={(event) => setFilter(event.target.value)}
          />
        </label>
        <label className="flex min-w-0 flex-col gap-1 overflow-clip">
          Sort table by
          <NativeSelect
            value={sort}
            onChange={(event) => setSort(Number(event.target.value))}
            className="w-full"
          >
            <option value={-1}>Original row order</option>
            {columns.map((column, index) => (
              <option key={index} value={index}>
                {column || "(unnamed)"} (column {index + 1})
              </option>
            ))}
          </NativeSelect>
        </label>
        <Button
          variant="outline"
          disabled={sort < 0}
          onClick={() => setDescending((value) => !value)}
        >
          {descending ? "Descending" : "Ascending"}
        </Button>
      </div>
      <p role="status" aria-live="polite">
        {filtered.length.toLocaleString("en-US")} of{" "}
        {rows.length.toLocaleString("en-US")} rows
      </p>
      {!filtered.length ? <p>No matching table rows.</p> : null}
      {columns.length > 0 ? (
        <div
          role="region"
          aria-label={`${title} scrollable data`}
          tabIndex={0}
          className="h-96 overflow-auto rounded-md border"
          onScroll={(event) => setScrollTop(event.currentTarget.scrollTop)}
        >
          <table
            className="table-fixed text-left text-sm"
            style={{ width: Math.max(1, columns.length) * 200 }}
            aria-rowcount={filtered.length + 1}
            aria-colcount={columns.length}
          >
            <caption className="sr-only">{title}</caption>
            <thead className="sticky top-0 z-10 bg-background">
              <tr className="h-8" aria-rowindex={1}>
                {columns.map((column, index) => (
                  <th
                    key={index}
                    scope="col"
                    className="truncate px-2"
                    title={column}
                  >
                    {column || `Unnamed column ${index + 1}`}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {start > 0 ? (
                <tr aria-hidden="true" style={{ height: start * rowHeight }}>
                  <td colSpan={columns.length} />
                </tr>
              ) : null}
              {filtered.slice(start, end).map((row, offset) => (
                <tr
                  key={start + offset}
                  className="h-8 border-b"
                  aria-rowindex={start + offset + 2}
                >
                  {row.map((cell, index) => (
                    <td key={index} className="truncate px-2" title={cell}>
                      {cell}
                    </td>
                  ))}
                </tr>
              ))}
              {end < filtered.length ? (
                <tr
                  aria-hidden="true"
                  style={{ height: (filtered.length - end) * rowHeight }}
                >
                  <td colSpan={columns.length} />
                </tr>
              ) : null}
            </tbody>
          </table>
        </div>
      ) : null}
    </section>
  )
}
