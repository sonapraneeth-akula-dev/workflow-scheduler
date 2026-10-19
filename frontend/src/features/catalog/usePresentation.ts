import { useQuery } from "@tanstack/react-query"
import {
  accessDenied,
  getTeams,
  type CatalogWorkflow,
  type Presentation,
} from "./api"

export function usePresentation(team: string) {
  const query = useQuery({
    queryKey: ["catalog", "teams"],
    queryFn: ({ signal }) => getTeams(signal),
    select: (teams) => {
      const found = teams.find((value) => value.team === team)
      if (!found) throw new Error("This team is not available to your account.")
      return found.presentation
    },
  })
  return { ...query, data: accessDenied(query.error) ? undefined : query.data }
}

export function orderTypes<T extends { name: string }>(
  types: readonly T[],
  config: Presentation | null
): T[] {
  const rank = new Map(config?.typeOrder.map((name, index) => [name, index]))
  return [...types].sort(
    (left, right) =>
      (rank.get(left.name) ?? rank.size) - (rank.get(right.name) ?? rank.size)
  )
}

export function metadataValue(
  workflow: CatalogWorkflow,
  key: string
): string | undefined {
  return Object.hasOwn(workflow.metadata.values, key)
    ? workflow.metadata.values[key]
    : undefined
}

export function presentationLabel(
  config: Presentation | null,
  key: string
): string {
  return config && Object.hasOwn(config.labels, key)
    ? (config.labels[key] ?? key)
    : key
}

export function presentationName(
  config: Presentation | null,
  value: string,
  id = value
): string {
  if (config?.names && Object.hasOwn(config.names, id)) return config.names[id]!
  return value
    .replace(/[-_]+/g, " ")
    .replace(/(^|\s)\p{L}/gu, (letter) => letter.toLocaleUpperCase())
}

export function groupWorkflows(
  workflows: readonly CatalogWorkflow[],
  config: Presentation | null
) {
  const keys = config?.groupingKeys ?? []
  const groups = new Map<
    string,
    { id: string; label: string | null; items: CatalogWorkflow[] }
  >()
  for (const workflow of workflows) {
    const values = keys.map((key) => metadataValue(workflow, key) ?? null)
    const id = JSON.stringify(values)
    let group = groups.get(id)
    if (!group) {
      group = {
        id,
        label: keys.length
          ? keys
              .map(
                (key, index) =>
                  `${presentationLabel(config, key)}: ${values[index] ?? "Not set"}`
              )
              .join(" / ")
          : null,
        items: [],
      }
      groups.set(id, group)
    }
    group.items.push(workflow)
  }
  return [...groups.values()]
}
