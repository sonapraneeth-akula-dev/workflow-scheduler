import { useEffect, useRef, useState } from "react"

type ContentState = {
  content: Blob
  pages: string[]
  page: number
  bytesRead: number
  complete: boolean
  pending: boolean
  error?: unknown
}

export function useProgressiveContent(content: Blob) {
  const [state, setState] = useState<ContentState | null>(null)
  const read = useRef<(() => Promise<void>) | null>(null)
  useEffect(() => {
    let active = true
    let pending = false
    let offset = 0
    const pages: string[] = []
    const decoder = new TextDecoder("utf-8", { fatal: true })
    const next = async () => {
      if (pending || !active || (offset === content.size && pages.length))
        return
      pending = true
      setState((old) =>
        old?.content === content ? { ...old, pending: true } : null
      )
      try {
        const end = Math.min(content.size, offset + 64 * 1024)
        const bytes = await content.slice(offset, end).arrayBuffer()
        if (!active) return
        let text: string
        try {
          text = decoder.decode(bytes, { stream: end < content.size })
        } catch {
          throw new Error("Report content is not valid UTF-8 text.")
        }
        offset = end
        pages.push(text)
        setState({
          content,
          pages: [...pages],
          page: pages.length - 1,
          bytesRead: offset,
          complete: offset === content.size,
          pending: false,
        })
      } catch (error) {
        if (active)
          setState({
            content,
            pages: [],
            page: 0,
            bytesRead: offset,
            complete: false,
            pending: false,
            error,
          })
      } finally {
        pending = false
      }
    }
    read.current = next
    void next()
    return () => {
      active = false
      if (read.current === next) read.current = null
    }
  }, [content])
  const current = state?.content === content ? state : null
  return {
    ...current,
    text: current?.pages[current.page],
    next: () => {
      if (current && current.page + 1 < current.pages.length)
        setState({ ...current, page: current.page + 1 })
      else void read.current?.()
    },
    previous: () => {
      if (current && current.page > 0)
        setState({ ...current, page: current.page - 1 })
    },
  }
}
