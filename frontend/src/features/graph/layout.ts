import type { CSSProperties } from "react"
import type { VersionPlan } from "@/features/taskplan/api"

type Definition = Pick<VersionPlan["nodes"][number], "key" | "dependencies">
export interface GraphPosition {
  readonly key: string
  readonly rank: number
  readonly style: CSSProperties & {
    left: number
    top: number
    width: number
    height: number
  }
}
export interface GraphEdge {
  readonly from: string
  readonly to: string
  readonly when: Definition["dependencies"][number]["when"]
  readonly path: string
}
export interface GraphLayout {
  readonly nodes: readonly GraphPosition[]
  readonly edges: readonly GraphEdge[]
  readonly width: number
  readonly height: number
}

export function layoutGraph(definitions: readonly Definition[]): GraphLayout {
  const ranks = new Map<string, number>()
  const pending = new Map<string, number>()
  const children = new Map<string, string[]>()
  for (const node of definitions) {
    if (pending.has(node.key))
      throw new Error("The graph contains duplicate nodes.")
    if (
      new Set(node.dependencies.map((dependency) => dependency.node)).size !==
      node.dependencies.length
    )
      throw new Error("The graph contains duplicate dependencies.")
    pending.set(node.key, node.dependencies.length)
    ranks.set(node.key, 0)
    children.set(node.key, [])
  }
  for (const node of definitions)
    for (const dependency of node.dependencies) {
      const targets = children.get(dependency.node)
      if (!targets) throw new Error("The graph depends on an unknown node.")
      targets.push(node.key)
    }
  const queue = definitions
    .filter((node) => node.dependencies.length === 0)
    .map((node) => node.key)
  for (let index = 0; index < queue.length; index++) {
    const key = queue[index]!
    for (const child of children.get(key)!) {
      ranks.set(child, Math.max(ranks.get(child)!, ranks.get(key)! + 1))
      const remaining = pending.get(child)! - 1
      pending.set(child, remaining)
      if (remaining === 0) queue.push(child)
    }
  }
  if (queue.length !== definitions.length)
    throw new Error("The graph contains a dependency cycle.")
  const rows = new Map<number, number>()
  const nodes = definitions.map((node): GraphPosition => {
    const rank = ranks.get(node.key)!
    const row = rows.get(rank) ?? 0
    rows.set(rank, row + 1)
    return {
      key: node.key,
      rank,
      style: {
        left: 16 + rank * 208,
        top: 16 + row * 84,
        width: 160,
        height: 64,
      },
    }
  })
  const tallest = Math.max(0, ...rows.values())
  for (const node of nodes)
    node.style.top += (tallest - rows.get(node.rank)!) * 42
  const byKey = new Map(nodes.map((node) => [node.key, node]))
  function ports(key: string, neighbours: readonly string[]) {
    const node = byKey.get(key)!
    return new Map(
      [...neighbours]
        .sort((left, right) => {
          const a = byKey.get(left)!
          const b = byKey.get(right)!
          // Skipped-column edges leave/enter at the top before using the outer rail.
          return (
            Number(Math.abs(b.rank - node.rank) > 1) -
              Number(Math.abs(a.rank - node.rank) > 1) ||
            a.style.top - b.style.top ||
            a.style.left - b.style.left
          )
        })
        .map((neighbour, index) => [
          neighbour,
          Math.round(
            node.style.top +
              (node.style.height * (index + 1)) / (neighbours.length + 1)
          ),
        ])
    )
  }
  const exits = new Map(
    nodes.map((node) => [node.key, ports(node.key, children.get(node.key)!)])
  )
  const entrances = new Map(
    definitions.map((node) => [
      node.key,
      ports(
        node.key,
        node.dependencies.map((dependency) => dependency.node)
      ),
    ])
  )
  const edges = definitions.flatMap((node) =>
    node.dependencies.map((dependency): GraphEdge => {
      const from = byKey.get(dependency.node)!
      const to = byKey.get(node.key)!
      const x = from.style.left + from.style.width,
        y = exits.get(dependency.node)!.get(node.key)!
      const endX = to.style.left - 4,
        endY = entrances.get(node.key)!.get(dependency.node)!
      const bend = Math.min(24, (endX - x) / 2)
      return {
        from: dependency.node,
        to: node.key,
        when: dependency.when,
        path:
          to.rank - from.rank > 1
            ? `M${x} ${y} H${x + 8} V8 H${endX - 12} V${endY} H${endX}`
            : `M${x} ${y} C${x + bend} ${y} ${endX - bend} ${endY} ${endX} ${endY}`,
      }
    })
  )
  return {
    nodes,
    edges,
    width:
      Math.max(0, ...nodes.map((node) => node.style.left + node.style.width)) +
      16,
    height:
      Math.max(0, ...nodes.map((node) => node.style.top + node.style.height)) +
      16,
  }
}
