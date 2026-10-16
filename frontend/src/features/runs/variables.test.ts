import { afterEach, describe, expect, it, vi } from "vitest"
import { JsonNumberSupportError, parseJson } from "@/lib/json"
import {
  parseVariable,
  triggerSchema,
  validateVariables,
  variablesDraft,
  type Variable,
} from "./variables"

const id = "019ed132-2f44-7000-8000-000000000001"
function definition(
  type: Variable["type"],
  extra: Partial<Variable> = {}
): Variable {
  return {
    key: "value",
    type,
    required: true,
    default: null,
    allowedValues: [],
    ...extra,
  }
}
afterEach(() => vi.unstubAllGlobals())

describe("schema-driven run variables", () => {
  it.each([
    "1",
    "-1",
    "1.02:03",
    "1:2",
    "1:2:3",
    "001:02:03",
    "-01:02",
    "01:02:.5",
    "1.2:3:4",
    "01:02:03.1234567 ",
    "00000000001.00:00:00",
    " 01:02:03 ",
    "\t01:02:03\t",
    "01:02",
    "01:02:03.",
  ])("matches the actual .NET constant parser's accepted form %j", (text) => {
    expect(parseVariable(definition("Duration"), text)).toBe(text)
  })
  it.each([
    "1.2",
    "00:00:00.12345678",
    "01:02:03.00000000",
    "01:02:",
    "01:02.5",
    "\t01:02:03\r\n",
    ".01:02:03",
    "+01:02:03",
  ])("matches the actual .NET constant parser's rejected form %j", (text) => {
    expect(() => parseVariable(definition("Duration"), text)).toThrow()
  })
  it.each([
    ["String", "", '""'],
    ["Int", "-2147483648", "-2147483648"],
    ["Int", "2147483647", "2147483647"],
    ["Long", "9223372036854775807", "9223372036854775807"],
    ["Long", "-9223372036854775808", "-9223372036854775808"],
    ["Double", "1.25e30", "1.25e30"],
    ["Double", "-0.0001", "-0.0001"],
    ["Bool", "false", "false"],
    ["StringList", '["", "line\\nbreak", "☃"]', '["","line\\nbreak","☃"]'],
    ["Duration", "1.02:03:04.1234567", '"1.02:03:04.1234567"'],
    ["Duration", "-10675199.02:48:05.4775808", '"-10675199.02:48:05.4775808"'],
    ["Duration", "10675199.02:48:05.4775807", '"10675199.02:48:05.4775807"'],
    [
      "Secret",
      "secret://vault/token.name",
      '{"Name":"secret://vault/token.name"}',
    ],
    ["Secret", "local/name", '{"Name":"local/name"}'],
  ] as const)(
    "accepts %s input %s without changing its value",
    (type, text, expected) => {
      expect(JSON.stringify(parseVariable(definition(type), text))).toBe(
        expected
      )
    }
  )
  it.each([
    ["Int", "2147483648"],
    ["Int", "-2147483649"],
    ["Int", "1.0"],
    ["Long", "9223372036854775808"],
    ["Long", "-9223372036854775809"],
    ["Long", "1e2"],
    ["Int", "01"],
    ["Int", "+1"],
    ["Double", "1e999"],
    ["Double", '"1"'],
    ["Double", "null"],
    ["Double", "NaN"],
    ["Bool", "False"],
    ["Bool", "0"],
    ["StringList", '["ok",1]'],
    ["StringList", "{}"],
    ["StringList", '["\\u0000"]'],
    ["String", "\0"],
    ["Duration", "5 minutes"],
    ["Duration", "24:00:00"],
    ["Duration", "00:60:00"],
    ["Duration", "00:00:60"],
    ["Duration", "10675199.02:48:05.4775808"],
    ["Duration", "-10675199.02:48:05.4775809"],
    ["Duration", "00:00:00.12345678"],
    ["Secret", "../token"],
    ["Secret", "secret://Vault/name"],
    ["Secret", "secret://vault-/name"],
    ["Secret", "secret://vault/../name"],
    ["Secret", "literal value!"],
  ] as const)("rejects invalid %s input %s", (type, text) => {
    expect(() => parseVariable(definition(type), text)).toThrow()
  })
  it("honors exact case-sensitive string/enum allowed values, including empty strings", () => {
    for (const type of ["String", "Enum"] as const) {
      const schema = definition(type, { allowedValues: ["", "Production"] })
      expect(parseVariable(schema, "")).toBe("")
      expect(parseVariable(schema, "Production")).toBe("Production")
      expect(() => parseVariable(schema, "production")).toThrow(
        "allowed values"
      )
    }
  })
  it("prefills every type, keeps Int64 tokens and preserves optional default omission versus empty/false", () => {
    const definitions: Variable[] = [
      definition("Long", {
        key: "long",
        default: parseJson("9223372036854775807") as Variable["default"],
      }),
      definition("Bool", { key: "bool", default: false }),
      definition("String", { key: "empty", required: false, default: "" }),
      definition("StringList", { key: "list", default: ["a", ""] }),
      definition("Secret", { key: "secret", default: { Name: "token" } }),
      definition("String", { key: "optional", required: false }),
    ]
    const draft = variablesDraft(definitions)
    expect(draft).toEqual({
      long: "9223372036854775807",
      bool: "false",
      empty: "",
      list: '["a",""]',
      secret: "token",
      optional: undefined,
    })
    expect(
      JSON.stringify(validateVariables(definitions, draft).variables)
    ).toBe(
      '{"long":9223372036854775807,"bool":false,"empty":"","list":["a",""],"secret":{"Name":"token"}}'
    )
    const omitted = validateVariables(definitions, {
      ...draft,
      empty: undefined,
    })
    expect(omitted.errors.size).toBe(0)
    expect(Object.hasOwn(omitted.variables, "empty")).toBe(false)
  })
  it("reports required/malformed/undeclared fields without including their supplied values", () => {
    const result = validateVariables(
      [definition("String"), definition("Int", { key: "count" })],
      { count: "sensitive-invalid-input", unknown: "private" }
    )
    expect([...result.errors.keys()]).toEqual(["unknown", "value", "count"])
    expect([...result.errors.values()].join()).not.toMatch(
      /sensitive-invalid-input|private/
    )
  })
  it("preserves own special keys without inheriting prototype variables", () => {
    const definitions = ["__proto__", "constructor", "toString"].map((key) =>
      definition("String", { key, default: key })
    )
    const result = validateVariables(definitions, variablesDraft(definitions))
    expect(Object.keys(result.variables)).toEqual([
      "__proto__",
      "constructor",
      "toString",
    ])
    expect(result.variables.__proto__).toBe("__proto__")
    expect(
      validateVariables(
        [definition("String", { key: "toString" })],
        {}
      ).errors.has("toString")
    ).toBe(true)
  })
  it("fails explicitly when this browser cannot preserve Int64 values", () => {
    vi.stubGlobal("JSON", { parse: JSON.parse, stringify: JSON.stringify })
    expect(() =>
      parseVariable(definition("Long"), "9223372036854775807")
    ).toThrow(JsonNumberSupportError)
  })
  it.each([
    [definition("Int", { default: "wrong" })],
    [definition("Enum")],
    [definition("Bool", { allowedValues: ["true"] })],
    [definition("String", { allowedValues: ["a", "a"] })],
    [definition("Secret", { default: { Name: "token", Value: "never" } })],
    [definition("Int"), definition("String")],
  ])(
    "rejects inconsistent API schemas instead of rendering a partial form",
    (...variables) => {
      expect(() =>
        triggerSchema.parse({
          workflowId: "t/k/w",
          versionId: id,
          version: "1.0.0",
          variables,
        })
      ).toThrow()
    }
  )
})
