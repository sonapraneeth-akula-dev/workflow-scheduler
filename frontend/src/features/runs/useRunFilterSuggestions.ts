import { useInfiniteQuery, useQuery } from "@tanstack/react-query"
import { accessDenied, getTypes, getWorkflows } from "@/features/catalog/api"

export function useRunFilterSuggestions(
  team: string | undefined,
  type: string
) {
  const types = useQuery({
    queryKey: ["catalog", "team", team, "filter-types"],
    queryFn: ({ signal }) => getTypes(team!, signal),
    enabled: Boolean(team),
  })
  const permittedTypes = accessDenied(types.error) ? undefined : types.data
  const selected = permittedTypes?.find((entry) => entry.name === type)
  const workflows = useInfiniteQuery({
    queryKey: ["catalog", "team", team, "filter-workflows", type],
    initialPageParam: null as string | null,
    queryFn: ({ pageParam, signal }) =>
      getWorkflows(team!, type, pageParam, signal),
    getNextPageParam: (page) => page.nextCursor ?? undefined,
    enabled: Boolean(team && selected),
  })
  const items =
    selected && !accessDenied(workflows.error)
      ? (workflows.data?.pages.flatMap((page) => page.items) ?? [])
      : []
  const fields = new Map<string, Set<string>>()
  for (const field of selected?.metadataSchema.keys ?? []) {
    fields.set(field.key, new Set(field.allowedValues))
  }
  for (const item of items) {
    for (const [key, value] of Object.entries(item.metadata.values)) {
      if (!fields.has(key)) fields.set(key, new Set())
      fields.get(key)!.add(value)
    }
  }
  return {
    types: permittedTypes ?? [],
    workflows: items,
    metadata: fields,
    error: types.error ?? (selected ? workflows.error : null),
    loading: types.isFetching || workflows.isFetching,
    hasMore: Boolean(selected && workflows.hasNextPage),
    loadMore: () => void workflows.fetchNextPage(),
    retry: () =>
      void (types.error || !selected ? types.refetch() : workflows.refetch()),
  }
}
