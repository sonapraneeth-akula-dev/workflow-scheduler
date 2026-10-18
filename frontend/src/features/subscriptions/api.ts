import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import type { components } from "@/lib/api/schema"
import { runFilterSchema } from "@/features/runs/search"
import { secretName } from "@/features/runs/variables"

const uuid = z
  .uuid()
  .refine(
    (value) =>
      value === value.toLowerCase() &&
      value !== "00000000-0000-0000-0000-000000000000"
  )
const workflow = runFilterSchema.shape.workflowId
  .unwrap()
  .refine((value) =>
    value
      .split("/")
      .every(
        (part) =>
          part.trim().length > 0 &&
          part !== "." &&
          part !== ".." &&
          [...part].length <= 128
      )
  )
const scopeFields = {
  scope: z.enum(["workflow", "schedule", "type"]),
  scopeId: z.string().min(1).max(1024),
}
export const subscriptionScopeSchema = z
  .strictObject(scopeFields)
  .refine(
    ({ scope, scopeId }) =>
      scope === "schedule"
        ? uuid.safeParse(scopeId).success
        : workflow.safeParse(scope === "type" ? `${scopeId}/workflow` : scopeId)
            .success,
    "Use a workflow identity, type identity or canonical schedule UUID."
  )
export type SubscriptionScope = z.infer<typeof subscriptionScopeSchema>
export const channelSchema = z.enum(["Email", "Teams", "Webhook"])
export const eventSchema = z.enum(["Completed", "Failed"])
const configurationFields = {
  ...scopeFields,
  events: z
    .array(eventSchema)
    .min(1)
    .max(2)
    .refine((events) => new Set(events).size === events.length),
  channel: channelSchema,
  target: z
    .string()
    .min(1)
    .max(2048)
    .refine((value) => value === value.trim() && !/\p{Cc}/u.test(value)),
  secretRef: z.string().nullable(),
}
type Configuration = z.infer<z.ZodObject<typeof configurationFields>>

function webhookUrl(value: string): string | undefined {
  try {
    const url = new URL(value)
    const loopback =
      url.hostname === "localhost" ||
      url.hostname === "[::1]" ||
      /^127\.\d+\.\d+\.\d+$/.test(url.hostname)
    return !url.username &&
      !url.password &&
      !url.href.includes("?") &&
      !url.href.includes("#") &&
      (url.protocol === "https:" || (url.protocol === "http:" && loopback))
      ? url.href
      : undefined
  } catch (error) {
    if (!(error instanceof TypeError)) throw error
    return undefined
  }
}

function validConfiguration(value: Configuration, context: z.RefinementCtx) {
  if (
    !subscriptionScopeSchema.safeParse({
      scope: value.scope,
      scopeId: value.scopeId,
    }).success
  )
    context.addIssue({
      code: "custom",
      path: ["scopeId"],
      message: "Invalid subscription scope.",
    })
  const valid =
    value.channel === "Webhook"
      ? webhookUrl(value.target) !== undefined &&
        value.secretRef !== null &&
        secretName(value.secretRef)
      : value.secretRef === null &&
        (value.channel === "Teams"
          ? secretName(value.target)
          : z.email().safeParse(value.target).success)
  if (!valid)
    context.addIssue({
      code: "custom",
      path: ["target"],
      message:
        value.channel === "Webhook"
          ? "Use an HTTPS URL without credentials, query or fragment (loopback HTTP allowed), and a safe signing secret reference."
          : value.channel === "Teams"
            ? "Use a configured Teams destination reference, never a literal webhook URL or credential."
            : "Use one email mailbox without a display name or signing reference.",
    })
}

export const subscriptionRequestSchema = z
  .strictObject(configurationFields)
  .superRefine(validConfiguration)
  .transform((value) => ({
    ...value,
    events: [...value.events].sort(),
    target:
      value.channel === "Webhook" ? webhookUrl(value.target)! : value.target,
  }))
export const subscriptionSchema = z
  .strictObject({
    ...configurationFields,
    id: uuid,
    createdAt: z.iso.datetime({ offset: true }),
  })
  .superRefine(validConfiguration)
  .refine(
    (value) =>
      value.channel !== "Webhook" || webhookUrl(value.target) === value.target,
    "Subscription destinations must be canonical."
  )
export type Subscription = z.infer<typeof subscriptionSchema>
const pageSchema = z.strictObject({
  items: z.array(subscriptionSchema).max(50),
  nextCursor: uuid.nullable(),
})
export type SubscriptionPage = z.infer<typeof pageSchema>

export async function getSubscriptions(
  resource: SubscriptionScope,
  cursor: string | null = null,
  signal?: AbortSignal
) {
  const scope = subscriptionScopeSchema.parse(resource)
  const after = cursor === null ? null : uuid.parse(cursor)
  const result = await publicApi.GET("/api/v1/subscriptions", {
    params: {
      query: { ...scope, limit: 50, ...(after ? { cursor: after } : {}) },
    },
    signal,
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  if (result.response.status !== 200)
    throw new Error("Subscription listing requires HTTP 200.")
  const data = pageSchema.parse(result.data)
  if (
    data.items.some(
      (row, index) =>
        row.scope !== scope.scope ||
        row.scopeId !== scope.scopeId ||
        (after !== null && row.id >= after) ||
        (index > 0 && row.id >= data.items[index - 1]!.id)
    ) ||
    (data.nextCursor !== null &&
      (data.items.length !== 50 || data.nextCursor !== data.items.at(-1)?.id))
  )
    throw new Error(
      "The API returned inconsistent subscription scope or pagination."
    )
  return data satisfies components["schemas"]["SubscriptionPage"]
}

export async function createSubscription(
  input: z.input<typeof subscriptionRequestSchema>
) {
  const request = subscriptionRequestSchema.parse(input)
  const result = await publicApi.POST("/api/v1/subscriptions", {
    body: request,
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  if (result.response.status !== 201)
    throw new Error("Subscription creation requires HTTP 201.")
  const row = subscriptionSchema.parse(result.data)
  if (
    row.scope !== request.scope ||
    row.scopeId !== request.scopeId ||
    row.channel !== request.channel ||
    row.target !== request.target ||
    row.secretRef !== request.secretRef ||
    row.events.join(",") !== request.events.join(",")
  )
    throw new Error("The API returned a different subscription configuration.")
  return row satisfies components["schemas"]["SubscriptionRow"]
}

export async function deleteSubscription(id: string) {
  const key = uuid.parse(id)
  const result = await publicApi.DELETE("/api/v1/subscriptions/{id}", {
    params: { path: { id: key } },
  })
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  if (result.response.status !== 204)
    throw new Error("Unsubscribe requires HTTP 204.")
}
