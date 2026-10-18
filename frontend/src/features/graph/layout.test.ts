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
      expect(edge.path).toMatch(/^M\d+ \d+ C\d+ \d+ \d+ \d+ \d+ \d+$/)
    }
    expect(layoutGraph(input)).toEqual(layout)
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
