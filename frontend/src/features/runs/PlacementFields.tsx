import { useId } from "react"
import { useQuery } from "@tanstack/react-query"
import { ErrorNotice } from "@/components/ErrorNotice"
import type { RunPlacement } from "@/lib/live/runApi"
import type { TriggerRequest } from "./trigger-api"
import { rerunPlacementOptions, triggerPlacementOptions } from "./placement-api"

export function PlacementFields({
  value,
  onChange,
  source,
  disabled,
  valid = true,
}: {
  value: RunPlacement | undefined
  onChange: (value: RunPlacement) => void
  source: TriggerRequest | { runId: string }
  disabled: boolean
  valid?: boolean
}) {
  const id = useId()
  const query = useQuery({
    queryKey: ["placement-options", source],
    queryFn: ({ signal }) =>
      "runId" in source
        ? rerunPlacementOptions(source.runId, signal)
        : triggerPlacementOptions(source, signal),
    enabled: value?.singleMachine === true && valid && !disabled,
    retry: false,
    staleTime: 0,
    refetchInterval: 10_000,
  })
  return (
    <fieldset disabled={disabled} className="space-y-2">
      <legend className="font-semibold">Placement override</legend>
      <label className="flex items-center gap-2">
        <input
          type="checkbox"
          checked={value?.singleMachine ?? false}
          onChange={(event) =>
            onChange({ singleMachine: event.target.checked })
          }
        />
        Run on a single machine
      </label>
      {value?.singleMachine ? (
        <>
          <label htmlFor={id}>Machine</label>
          <select
            id={id}
            value={value.machine ?? ""}
            disabled={!valid || query.isFetching || !!query.error}
            onChange={(event) =>
              onChange({
                singleMachine: true,
                ...(event.target.value ? { machine: event.target.value } : {}),
              })
            }
          >
            <option value="">Auto-pick eligible machine</option>
            {query.data?.map((machine) => (
              <option key={machine.id} value={machine.name}>
                {machine.name}
              </option>
            ))}
          </select>
          {!valid ? (
            <p>Complete valid variables to check eligible machines.</p>
          ) : null}
          {query.isFetching ? (
            <p role="status">Checking eligible machines...</p>
          ) : null}
          {query.error ? <ErrorNotice error={query.error} /> : null}
          {query.data?.length === 0 ? (
            <p role="status">
              No eligible single machine. Acceptance rechecks all selectors,
              health, drain and reservations.
            </p>
          ) : null}
          {value.machine &&
          !query.data?.some((machine) => machine.name === value.machine) ? (
            <p role="status">
              Selected machine is no longer eligible. Acceptance will revalidate
              placement.
            </p>
          ) : null}
        </>
      ) : null}
    </fieldset>
  )
}
