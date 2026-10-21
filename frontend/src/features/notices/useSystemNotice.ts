import { useQuery } from "@tanstack/react-query"
import { getSystemNotice, noticeQueryKey } from "./api"

export function useSystemNotice() {
  return useQuery({
    queryKey: noticeQueryKey,
    queryFn: ({ signal }) => getSystemNotice(signal),
    retry: false,
    staleTime: 15_000,
    refetchInterval: 30_000,
  })
}
