import { z } from "zod"

export const logIdentitySchema = z
  .string()
  .max(2048)
  .refine((id) => {
    const [run, node, task, attempt, extra] = id.split("/")
    return (
      extra === undefined &&
      run !== undefined &&
      z.uuid().safeParse(run).success &&
      run === run.toLowerCase() &&
      run[14] === "7" &&
      [node, task].every(
        (key) =>
          key &&
          key.trim().length > 0 &&
          !/[\p{Cc}]/u.test(key) &&
          new TextEncoder().encode(key).length <= 512
      ) &&
      /^[1-9]\d*$/.test(attempt ?? "") &&
      Number(attempt) <= 32767
    )
  }, "A canonical task-attempt log identity is required.")

export const liveLogRecordSchema = z.object({
  line: z.int().positive(),
  at: z.iso.datetime({ offset: true }),
  stream: z.enum(["stdout", "stderr", "ctx"]),
  level: z.enum(["Information", "Warning", "Error", "Debug"]),
  text: z.string().max(65536),
})

const batchSchema = z
  .object({
    id: logIdentitySchema,
    records: z.array(liveLogRecordSchema).min(1).max(256),
    gap: z.boolean(),
  })
  .refine(
    ({ records }) =>
      records.every((record, index) => {
        const previous = records[index - 1]
        return (
          index === 0 ||
          (previous !== undefined && record.line === previous.line + 1)
        )
      }),
    "Live log records must be contiguous and ordered."
  )

export type LiveLogRecord = z.infer<typeof liveLogRecordSchema>
export type LiveLogBatch = z.infer<typeof batchSchema>
export const parseLogBatch = (value: unknown) => batchSchema.parse(value)
