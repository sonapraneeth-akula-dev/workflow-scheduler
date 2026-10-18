import type { LiveLogBatch, LiveLogRecord } from "@/lib/live/logs"

export interface LogTail {
  records: LiveLogRecord[]
  lastLine: number
  gap: boolean
}

export const emptyLogTail: LogTail = { records: [], lastLine: 0, gap: false }

export function appendLogTail(tail: LogTail, batch: LiveLogBatch): LogTail {
  const added = batch.records.filter((record) => record.line > tail.lastLine)
  const last = added.at(-1)
  if (!last) return tail
  const records = [...tail.records, ...added]
  let bytes = records.reduce((sum, record) => sum + record.text.length * 2, 0)
  let start = 0
  while (records.length - start > 1000 || bytes > 1024 * 1024) {
    const record = records[start++]
    if (!record) throw new Error("Invalid bounded log tail cursor.")
    bytes -= record.text.length * 2
  }
  return {
    records: records.slice(start),
    lastLine: last.line,
    gap:
      tail.gap ||
      batch.gap ||
      (tail.lastLine > 0 && added[0]?.line !== tail.lastLine + 1),
  }
}
