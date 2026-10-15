import { afterEach, describe, expect, it, vi } from "vitest"
import { JsonNumberSupportError, jsonObjectSchema, parseJson } from "./json"

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

describe("lossless JSON variable boundaries", () => {
  it.each([
    "9223372036854775807",
    "-9223372036854775808",
    "9007199254740993",
    "1000000000000000128",
    "1e30",
  ])("preserves the exact number token %s", (source) => {
    const json = `{"value":${source}}`
    const parsed = jsonObjectSchema.parse(parseJson(json))
    expect(JSON.stringify(parsed)).toBe(json)
  })

  it("preserves large numbers nested in arrays and objects without changing strings", () => {
    const source =
      '{"nested":[9223372036854775807,{"value":-9223372036854775808}],"text":"9223372036854775807","flag":true,"none":null}'
    expect(JSON.stringify(jsonObjectSchema.parse(parseJson(source)))).toBe(
      source
    )
  })

  it("leaves safe native numbers as numbers", () => {
    expect(
      jsonObjectSchema.parse(
        parseJson('{"small":3,"fraction":0.25,"limit":9007199254740991}')
      )
    ).toEqual({ small: 3, fraction: 0.25, limit: Number.MAX_SAFE_INTEGER })
  })

  it("does not mistake ordinary rawJSON properties for native raw values", () => {
    const source = '{"value":{"rawJSON":"9223372036854775807"}}'
    expect(JSON.stringify(jsonObjectSchema.parse(parseJson(source)))).toBe(
      source
    )
  })

  it("fails explicitly rather than rounding when native raw serialization is missing", () => {
    vi.stubGlobal("JSON", {
      parse: JSON.parse,
      stringify: JSON.stringify,
      isRawJSON: JSON.isRawJSON,
    })
    expect(() => parseJson('{"value":9223372036854775807}')).toThrow(
      JsonNumberSupportError
    )
    expect(JSON.stringify(parseJson('{"value":2}'))).toBe('{"value":2}')
  })
})
