import { z } from "zod"
import { ApiError, publicApi } from "@/lib/api/client"
import { registrationSchema } from "@/features/versions/api"

const key = z
  .string()
  .min(1)
  .regex(/^[^/\\\p{Cc}]+$/u)
const selectorValue = z
  .object({
    literal: z.string().min(1).nullable(),
    variable: z.string().min(1).nullable(),
  })
  .refine((value) => (value.literal === null) !== (value.variable === null), {
    message:
      "A placement selector must declare exactly one literal or variable.",
  })
const placement = z
  .object({
    selector: z
      .object({
        os: selectorValue.nullable(),
        pool: selectorValue.nullable(),
        labels: z.array(
          z.object({ key: z.string(), value: selectorValue.nullable() })
        ),
        machines: z.array(selectorValue),
        machineIds: z
          .array(
            z
              .uuid()
              .refine((id) => id !== "00000000-0000-0000-0000-000000000000")
          )
          .optional(),
        count: z.int().positive().nullable().optional(),
      })
      .nullable(),
    affinity: z
      .object({
        kind: z.enum(["None", "Node", "Run", "SameMachineAs"]),
        nodeKey: z.string().nullable(),
      })
      .nullable(),
  })
  .nullable()
const failure = z.enum(["Hard", "Soft"])
const categories = z.enum([
  "TaskError",
  "Timeout",
  "AgentLost",
  "PlatformError",
  "ValidationError",
  "Aborted",
  "ResourceLimit",
  "Placement",
])
const task = z.object({
  key,
  action: z
    .object({
      kind: z.enum(["SdkFunction", "Command", "Http", "HttpOperation"]),
      declaration: z
        .string()
        .min(1)
        .max(4096)
        .regex(/^[^\p{Cc}]+$/u),
    })
    .nullable(),
  secretReference: z.string().nullable(),
  requestTimeout: z.string().nullable(),
  failure,
  policy: z.object({
    attemptTimeout: z.string(),
    autoRetry: z.boolean(),
    retry: z.object({
      retries: z.int().nonnegative(),
      on: z.array(categories),
      backoff: z.object({
        kind: z.enum(["Fixed", "Exponential"]),
        delay: z.string(),
        maxDelay: z.string(),
      }),
    }),
  }),
  placement,
})

export const versionPlanSchema = z
  .object({
    versionId: z.uuid(),
    workflowId: z.string().regex(/^[^/]+\/[^/]+\/[^/]+$/),
    version: z.string().min(1),
    runTimeout: z.string(),
    stages: z.array(key),
    placement,
    registration: registrationSchema.optional(),
    nodes: z.array(
      z.object({
        key,
        dependencies: z.array(
          z.object({
            node: key,
            when: z.enum(["OnSuccess", "OnFailure", "Always"]),
          })
        ),
        mode: z.enum(["Series", "Parallel"]),
        maxParallel: z.int().positive().nullable(),
        failure,
        timeout: z.string().nullable(),
        placement,
        tasks: z.array(task),
      })
    ),
  })
  .superRefine((plan, context) => {
    const nodes = new Set(plan.nodes.map((node) => node.key))
    if (
      nodes.size !== plan.nodes.length ||
      plan.stages.some((stage) => !nodes.has(stage)) ||
      plan.nodes.some(
        (node) =>
          new Set(node.tasks.map((task) => task.key)).size !==
            node.tasks.length ||
          new Set(node.dependencies.map((dependency) => dependency.node))
            .size !== node.dependencies.length ||
          node.dependencies.some(
            (dependency) =>
              !nodes.has(dependency.node) || dependency.node === node.key
          )
      )
    )
      context.addIssue({
        code: "custom",
        message: "The task plan contains inconsistent graph identities.",
      })
  })

export type VersionPlan = z.infer<typeof versionPlanSchema>
export type PlanPlacement = z.infer<typeof placement>

export async function getVersionPlan(
  workflowId: string,
  versionId: string,
  signal?: AbortSignal
) {
  const [team, type, workflow] = z
    .tuple([key, key, key])
    .parse(workflowId.split("/"))
  z.uuid().parse(versionId)
  const result = await publicApi.GET(
    "/api/v1/workflows/{team}/{type}/{workflow}/versions/{version}",
    { params: { path: { team, type, workflow, version: versionId } }, signal }
  )
  if (!result.response.ok) throw new ApiError(result.response, result.error)
  const plan = versionPlanSchema.parse(result.data)
  if (plan.workflowId !== workflowId || plan.versionId !== versionId)
    throw new Error(
      "The API returned a different workflow version's task plan."
    )
  return plan
}
