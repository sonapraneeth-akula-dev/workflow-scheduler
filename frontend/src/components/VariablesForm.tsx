import { useId } from "react"
import {
  Field,
  FieldDescription,
  FieldError,
  FieldGroup,
  FieldLabel,
} from "@/components/ui/field"
import { Input } from "@/components/ui/input"
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select"
import { Textarea } from "@/components/ui/textarea"
import type { Variable, VariablesDraft } from "@/features/runs/variables"

export function VariablesForm({
  definitions,
  draft,
  errors,
  disabled = false,
  onChange,
}: {
  definitions: readonly Variable[]
  draft: VariablesDraft
  errors: ReadonlyMap<string, string>
  disabled?: boolean
  onChange: (draft: VariablesDraft) => void
}) {
  const prefix = useId()
  return (
    <fieldset disabled={disabled} className="min-w-0">
      <legend className="mb-3 font-semibold">Run variables</legend>
      {definitions.length === 0 ? (
        <p>No variables declared by this version.</p>
      ) : null}
      <FieldGroup>
        {definitions.map((definition, index) => {
          const id = `${prefix}-${index}`
          const enabled =
            Object.hasOwn(draft, definition.key) &&
            draft[definition.key] !== undefined
          const error = errors.get(definition.key)
          const update = (text: string | undefined) =>
            onChange({ ...draft, [definition.key]: text })
          const options =
            definition.type === "Bool"
              ? ["true", "false"]
              : definition.allowedValues
          const common = {
            id,
            value: enabled ? (draft[definition.key] ?? "") : "",
            disabled: !enabled && !definition.required,
            "aria-invalid": Boolean(error),
            "aria-required": definition.required,
            "aria-describedby": `${id}-help${error ? ` ${id}-error` : ""}`,
          }
          return (
            <Field key={definition.key} data-invalid={Boolean(error)}>
              <FieldLabel htmlFor={id} className="break-all">
                {definition.key}
              </FieldLabel>
              {!definition.required ? (
                <label className="flex items-center gap-2 text-sm">
                  <input
                    type="checkbox"
                    checked={enabled}
                    onChange={(event) =>
                      update(
                        event.target.checked
                          ? definition.type === "StringList"
                            ? "[]"
                            : definition.type === "Bool"
                              ? "false"
                              : ""
                          : undefined
                      )
                    }
                  />
                  <span className="break-all">Set {definition.key}</span>
                </label>
              ) : null}
              {options.length ? (
                <NativeSelect
                  {...common}
                  value={
                    enabled
                      ? String(options.indexOf(draft[definition.key] ?? ""))
                      : ""
                  }
                  onChange={(event) =>
                    update(options[Number(event.target.value)]!)
                  }
                >
                  <NativeSelectOption value="" disabled>
                    Choose a value
                  </NativeSelectOption>
                  {options.map((option, index) => (
                    <NativeSelectOption key={option} value={String(index)}>
                      {option || "(empty string)"}
                    </NativeSelectOption>
                  ))}
                </NativeSelect>
              ) : definition.type === "StringList" ? (
                <Textarea
                  {...common}
                  spellCheck={false}
                  onChange={(event) => update(event.target.value)}
                />
              ) : (
                <Input
                  {...common}
                  type="text"
                  autoComplete="off"
                  spellCheck={false}
                  inputMode={
                    ["Int", "Long", "Double"].includes(definition.type)
                      ? "decimal"
                      : undefined
                  }
                  onChange={(event) => update(event.target.value)}
                />
              )}
              <FieldDescription id={`${id}-help`}>
                {definition.type};{" "}
                {definition.required ? "required" : "optional"}.
                {definition.default !== null
                  ? " A workflow default is prefilled; unsetting an optional override uses that default."
                  : ""}
                {definition.type === "Secret"
                  ? " Reference name only; secret values are never requested or resolved here."
                  : ""}
                {definition.type === "StringList"
                  ? ' Enter a JSON string array, for example ["one", "two"].'
                  : ""}
                {definition.type === "Duration"
                  ? " Use [-][days.]hh:mm:ss[.fffffff]."
                  : ""}
              </FieldDescription>
              {error ? (
                <FieldError id={`${id}-error`}>{error}</FieldError>
              ) : null}
            </Field>
          )
        })}
      </FieldGroup>
    </fieldset>
  )
}
