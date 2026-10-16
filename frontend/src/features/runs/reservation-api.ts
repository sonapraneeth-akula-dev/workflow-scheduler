import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import type { components } from "@/lib/api/schema"
import { instantSchema } from "./search"

const runIdentity = z
  .uuid()
  .refine((value) => value === value.toLowerCase() && value[14] === "7")
const itemSchema = z
  .object({
    nodeKey: z
      .string()
      .min(1)
      .refine((value) => value.trim().length > 0 && !value.includes("/")),
    position: z.int().positive(),
    count: z.int().positive(),
    queuedAt: instantSchema,
    blockingReservationId: runIdentity.nullable(),
    blockingRunId: runIdentity.nullable(),
  })
  .refine(
    (item) =>
      (item.blockingReservationId === null) === (item.blockingRunId === null)
  )
const responseSchema = z.object({
  runId: runIdentity,
  items: z.array(itemSchema),
})
export type ReservationQueue = z.infer<typeof responseSchema>

export async function getReservationQueue(
  runId: string,
  signal?: AbortSignal
): Promise<ReservationQueue> {
  const id = runIdentity.parse(runId)
  const response = await publicApi.GET("/api/v1/runs/{id}/reservation-queue", {
    params: { path: { id } },
    signal,
    cache: "no-store",
  })
  if (!response.response.ok)
    throw new ApiError(response.response, response.error)
  const result = responseSchema.parse(response.data)
  if (
    result.runId !== id ||
    new Set(result.items.map((item) => item.nodeKey)).size !==
      result.items.length ||
    result.items.some(
      (item, index) =>
        index > 0 && item.position <= result.items[index - 1]!.position
    )
  )
    throw new Error("The API returned an inconsistent reservation queue.")
  return result satisfies components["schemas"]["ReservationQueueResponse"]
}
