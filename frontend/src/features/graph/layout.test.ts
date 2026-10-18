import { describe, expect, it } from "vitest"
import { layoutGraph } from "./layout"

describe("stable dependency graph layout", () => {
  it("uses the longest dependency rank and preserves manifest order within each rank", () => {
    const input = [
      {
        key: "join",
        dependencies: [
          { node: "root", when: "Always" as const },
          { node: "middle", when: "OnSuccess" as const },
        ],
      },
      { key: "root", dependencies: [] },
      { key: "peer", dependencies: [] },
      {
        key: "middle",
        dependencies: [{ node: "root", when: "OnFailure" as const }],
      },
    ]
    const layout = layoutGraph(input)
    expect(layout.nodes.map((node) => [node.key, node.rank])).toEqual([
      ["join", 2],
      ["root", 0],
      ["peer", 0],
      ["middle", 1],
    ])
    expect(layout.nodes[1]!.style.top).toBeLessThan(layout.nodes[2]!.style.top)
    for (const edge of layout.edges) {
      const from = layout.nodes.find((node) => node.key === edge.from)!
      const to = layout.nodes.find((node) => node.key === edge.to)!
      expect(from.style.left + from.style.width).toBeLessThan(to.style.left)
      expect(edge.path).toMatch(/^M\d+ \d+ [CH]/)
      expect(edge.path).not.toMatch(/NaN|Infinity/)
    }
    expect(layoutGraph(input)).toEqual(layout)
  })

  it("centers branch columns and uses distinct, vertically ordered split/join ports", () => {
    const layout = layoutGraph([
      { key: "start", dependencies: [] },
      { key: "upper", dependencies: [{ node: "start", when: "OnSuccess" }] },
      { key: "lower", dependencies: [{ node: "start", when: "OnSuccess" }] },
      {
        key: "join",
        dependencies: [
          { node: "lower", when: "OnSuccess" },
          { node: "upper", when: "OnSuccess" },
        ],
      },
    ])
    const [start, upper, lower, join] = layout.nodes
    expect(start!.style.top).toBe(join!.style.top)
    expect(start!.style.top).toBe((upper!.style.top + lower!.style.top) / 2)
    const exits = layout.edges
      .filter((edge) => edge.from === "start")
      .map((edge) => Number(/^M\d+ (\d+)/.exec(edge.path)![1]))
    expect(exits[0]).toBeLessThan(exits[1]!)
    const arrivals = layout.edges
      .filter((edge) => edge.to === "join")
      .map((edge) => Number(/ (\d+) H\d+$/.exec(edge.path)![1]))
    expect(arrivals[0]).toBeGreaterThan(arrivals[1]!)
    for (const edge of layout.edges) {
      const target = layout.nodes.find((node) => node.key === edge.to)!
      expect(edge.path).toMatch(new RegExp(` H${target.style.left - 2}$`))
    }
  })

  it("routes skipped-column dependencies above intermediate cards rather than through them", () => {
    const layout = layoutGraph([
      { key: "start", dependencies: [] },
      { key: "middle", dependencies: [{ node: "start", when: "OnSuccess" }] },
      {
        key: "join",
        dependencies: [
          { node: "start", when: "Always" },
          { node: "middle", when: "OnSuccess" },
        ],
      },
    ])
    const direct = layout.edges.find(
      (edge) => edge.from === "start" && edge.to === "join"
    )!
    expect(direct.path).toMatch(/^M\d+ \d+ H\d+ V8 H\d+ V\d+ H\d+$/)
    expect(layout.nodes.every((node) => node.style.top >= 16)).toBe(true)
    expect(layout.edges).toHaveLength(3)
  })

  it("positions 200 connected nodes without overlap and includes every edge", () => {
    const nodes = Array.from({ length: 200 }, (_, index) => ({
      key: `node-${index}`,
      dependencies:
        index < 10
          ? []
          : [{ node: `node-${index - 10}`, when: "OnSuccess" as const }],
    }))
    const layout = layoutGraph(nodes)
    expect(layout.nodes).toHaveLength(200)
    expect(layout.edges).toHaveLength(190)
    expect(
      new Set(
        layout.nodes.map((node) => `${node.style.left}/${node.style.top}`)
      ).size
    ).toBe(200)
    expect(layout.width).toBe(4144)
    expect(layout.height).toBe(852)
  })

  it("handles a 10,000-node dependency chain iteratively rather than overflowing the stack", () => {
    const nodes = Array.from({ length: 10_000 }, (_, index) => ({
      key: String(index),
      dependencies:
        index === 0
          ? []
          : [{ node: String(index - 1), when: "OnSuccess" as const }],
    }))
    const layout = layoutGraph(nodes)
    expect(layout.nodes.at(-1)?.rank).toBe(9999)
    expect(layout.edges).toHaveLength(9999)
  })

  it.each([
    [
      { key: "duplicate", dependencies: [] },
      { key: "duplicate", dependencies: [] },
    ],
    [
      {
        key: "missing",
        dependencies: [{ node: "absent", when: "Always" as const }],
      },
    ],
    [
      {
        key: "self",
        dependencies: [{ node: "self", when: "Always" as const }],
      },
    ],
    [
      { key: "a", dependencies: [{ node: "b", when: "Always" as const }] },
      { key: "b", dependencies: [{ node: "a", when: "Always" as const }] },
    ],
    [
      { key: "root", dependencies: [] },
      {
        key: "child",
        dependencies: [
          { node: "root", when: "Always" as const },
          { node: "root", when: "OnSuccess" as const },
        ],
      },
    ],
  ])(
    "rejects inconsistent topology instead of drawing invented edges",
    (...nodes) => {
      expect(() => layoutGraph(nodes)).toThrow()
    }
  )

  it("accepts an empty workflow", () => {
    expect(layoutGraph([])).toEqual({
      nodes: [],
      edges: [],
      width: 16,
      height: 16,
    })
  })
})
