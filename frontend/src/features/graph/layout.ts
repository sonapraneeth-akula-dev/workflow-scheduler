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
  const byKey = new Map(nodes.map((node) => [node.key, node]))
  const edges = definitions.flatMap((node) =>
    node.dependencies.map((dependency): GraphEdge => {
      const from = byKey.get(dependency.node)!.style
      const to = byKey.get(node.key)!.style
      const x = from.left + from.width,
        y = from.top + from.height / 2
      const endX = to.left,
        endY = to.top + to.height / 2
      return {
        from: dependency.node,
        to: node.key,
        when: dependency.when,
        path: `M${x} ${y} C${x + 24} ${y} ${endX - 24} ${endY} ${endX} ${endY}`,
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
