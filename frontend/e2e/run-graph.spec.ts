import { mkdir, readFile, writeFile } from "node:fs/promises"
import { resolve } from "node:path"
import { expect, test } from "@playwright/test"
import { z } from "zod"
import { analyzeGraphTrace } from "../scripts/graph-trace"

test.use({ launchOptions: { args: ["--disable-frame-rate-limit"] } })

const settings = z
  .object({
    run: z.uuid(),
    token: z.string().min(1),
    initialSeq: z.coerce.number().int().nonnegative(),
    artifacts: z.string().min(1),
  })
  .parse({
    run: process.env.WF_GRAPH_RUN,
    token: process.env.WF_GRAPH_TOKEN,
    initialSeq: process.env.WF_GRAPH_INITIAL_SEQ,
    artifacts: process.env.WF_GRAPH_ARTIFACTS,
  })

test("200-node pinned DAG retains cached layout and draws 60 fps during 100 committed events/s", async ({
  page,
  browser,
}) => {
  test.setTimeout(120_000)
  await page.setViewportSize({ width: 1440, height: 1080 })
  await page.goto("/")
  await page.evaluate(async (token) => {
    const path = "/browser-auth.js"
    const { hydrateBrowserSession }: typeof import("../scripts/browser-auth") =
      await import(path)
    await hydrateBrowserSession(token)
  }, settings.token)
  await page.goto(`/runs/${settings.run}`)
  const graph = page.getByRole("region", {
    name: "Run dependency graph",
    exact: true,
  })
  await expect(graph).toHaveAttribute(
    "data-run-seq",
    String(settings.initialSeq)
  )
  await expect(page.locator("[data-graph-node]")).toHaveCount(200)
  await expect(page.locator("[data-graph-edge]")).toHaveCount(361)
  const initialLayout = await page
    .locator("[data-graph-node]")
    .evaluateAll((nodes) =>
      nodes.map((node) => ({
        key: node.getAttribute("data-graph-node"),
        style: node.getAttribute("style"),
      }))
    )
  const initialEdges = await page
    .locator("[data-graph-edge]")
    .evaluateAll((edges) => edges.map((edge) => edge.getAttribute("d")))
  await page.waitForTimeout(1500)
  const reads: string[] = []
  page.on("request", (request) => {
    const path = new URL(request.url()).pathname
    if (
      request.method() === "GET" &&
      path.startsWith("/api/") &&
      path !== `/api/v1/runs/${settings.run}/reservation-queue`
    )
      reads.push(request.url())
  })
  const session = await page.context().newCDPSession(page)
  const { frameTree } = z
    .object({ frameTree: z.object({ frame: z.object({ id: z.string() }) }) })
    .parse(await session.send("Page.getFrameTree"))
  await mkdir(settings.artifacts, { recursive: true })
  const tracePath = resolve(settings.artifacts, "chrome-trace.json")
  await session.send("Tracing.start", {
    categories:
      "devtools.timeline,disabled-by-default-devtools.timeline,disabled-by-default-devtools.timeline.frame,blink.user_timing",
    transferMode: "ReturnAsStream",
  })
  const observed = await page.evaluate(() => {
    const graph = document.querySelector<HTMLElement>(
      '[aria-label="Run dependency graph"]'
    )
    if (!graph) throw new Error("Graph viewport is absent.")
    let frame = 0,
      active = true,
      request = 0
    const changes: { frame: number; seq: number }[] = []
    const observer = new MutationObserver((records) => {
      if (records.some((record) => record.attributeName === "data-run-seq"))
        changes.push({ frame, seq: Number(graph.dataset.runSeq) })
    })
    observer.observe(graph, {
      attributes: true,
      attributeFilter: ["data-run-seq"],
    })
    const start = performance.now()
    const pan = (now: number) => {
      if (!active) return
      frame++
      graph.scrollLeft =
        ((Math.sin((now - start) / 900 - Math.PI / 2) + 1) / 2) *
        (graph.scrollWidth - graph.clientWidth)
      graph.scrollTop =
        ((Math.sin((now - start) / 1300 - Math.PI / 2) + 1) / 2) *
        (graph.scrollHeight - graph.clientHeight)
      request = requestAnimationFrame(pan)
    }
    request = requestAnimationFrame(pan)
    const stop = () => {
      active = false
      cancelAnimationFrame(request)
      observer.disconnect()
      performance.mark("graph-load-end")
      return { changes, animationFrames: frame }
    }
    Object.defineProperty(window, "__stopGraphTrace", {
      value: stop,
      configurable: true,
    })
    performance.mark("graph-load-start")
    return {
      userAgent: navigator.userAgent,
      hardwareConcurrency: navigator.hardwareConcurrency,
    }
  })
  let streamed: z.infer<typeof streamSchema>
  let updates: z.infer<typeof updatesSchema>
  try {
    const response = await page.request.post("/api/test/graph/stream", {
      headers: { Authorization: `Bearer ${settings.token}` },
      timeout: 30_000,
    })
    expect(response.status(), await response.text()).toBe(200)
    streamed = streamSchema.parse(await response.json())
    await expect(graph).toHaveAttribute(
      "data-run-seq",
      String(streamed.lastSeq)
    )
    updates = updatesSchema.parse(
      await page.evaluate(() => {
        const stop: unknown = Reflect.get(window, "__stopGraphTrace")
        if (typeof stop !== "function")
          throw new Error("Graph trace observer is missing.")
        return stop()
      })
    )
  } finally {
    const completed = new Promise<unknown>((resolve) =>
      session.once("Tracing.tracingComplete", resolve)
    )
    await session.send("Tracing.end")
    const result = z
      .object({ stream: z.string(), dataLossOccurred: z.boolean().optional() })
      .parse(await completed)
    const parts: Buffer[] = []
    try {
      while (true) {
        const part = z
          .object({
            data: z.string(),
            base64Encoded: z.boolean().optional(),
            eof: z.boolean(),
          })
          .parse(await session.send("IO.read", { handle: result.stream }))
        parts.push(
          Buffer.from(part.data, part.base64Encoded ? "base64" : "utf8")
        )
        if (part.eof) break
      }
    } finally {
      await session.send("IO.close", { handle: result.stream })
    }
    await writeFile(tracePath, Buffer.concat(parts))
    expect(
      result.dataLossOccurred ?? false,
      "Chrome trace must not lose events."
    ).toBe(false)
  }
  const frames = analyzeGraphTrace(
    JSON.parse(await readFile(tracePath, "utf8")),
    frameTree.frame.id
  )
  const report = {
    browserVersion: browser.version(),
    renderingMode:
      "Uncapped capacity (--disable-frame-rate-limit), not display refresh rate",
    ...observed,
    nodes: 200,
    edges: 361,
    transport:
      "Production signed-JWT HTTP / PostgreSQL / MessagePack / RunStore",
    interaction:
      "Continuous horizontal and vertical graph panning during real updates",
    ...streamed,
    actualEventsPerSecond: (streamed.count * 1000) / streamed.elapsedMs,
    uiFrameBatches: updates.changes.length,
    animationFrames: updates.animationFrames,
    animationFramesPerSecond:
      updates.animationFrames / frames.loadWindowSeconds,
    ...frames,
  }
  await writeFile(
    resolve(settings.artifacts, "report.json"),
    JSON.stringify(report, null, 2)
  )
  await test.info().attach("Chrome graph performance", {
    path: tracePath,
    contentType: "application/json",
  })
  console.log(JSON.stringify(report))
  expect(streamed.count).toBe(1000)
  expect(streamed.lastSeq).toBe(streamed.before + streamed.count)
  expect(report.actualEventsPerSecond).toBeGreaterThanOrEqual(100)
  expect(updates.changes.length).toBeGreaterThan(1)
  expect(new Set(updates.changes.map((change) => change.frame)).size).toBe(
    updates.changes.length
  )
  for (let index = 1; index < updates.changes.length; index++)
    expect(updates.changes[index]!.seq).toBeGreaterThan(
      updates.changes[index - 1]!.seq
    )
  expect(frames.fps).toBeGreaterThanOrEqual(60)
  expect(report.animationFramesPerSecond).toBeGreaterThanOrEqual(60)
  expect(reads).toEqual([])
  expect(
    await page.locator("[data-graph-node]").evaluateAll((nodes) =>
      nodes.map((node) => ({
        key: node.getAttribute("data-graph-node"),
        style: node.getAttribute("style"),
      }))
    )
  ).toEqual(initialLayout)
  expect(
    await page
      .locator("[data-graph-edge]")
      .evaluateAll((edges) => edges.map((edge) => edge.getAttribute("d")))
  ).toEqual(initialEdges)
  const final = await page.request.get(`/api/v1/runs/${settings.run}`, {
    headers: { Authorization: `Bearer ${settings.token}` },
  })
  expect(final.status()).toBe(200)
  const snapshot = z
    .object({
      lastSeq: z.number().int(),
      nodes: z.record(z.string(), z.number().int()),
    })
    .parse(await final.json())
  expect(snapshot.lastSeq).toBe(streamed.lastSeq)
  expect(
    await page
      .locator("[data-graph-node]")
      .evaluateAll((nodes) =>
        Object.fromEntries(
          nodes.map((node) => [
            node.getAttribute("data-graph-node"),
            Number(node.getAttribute("data-state")),
          ])
        )
      )
  ).toEqual(snapshot.nodes)
})

const streamSchema = z.object({
  before: z.number().int(),
  lastSeq: z.number().int(),
  count: z.number().int().positive(),
  elapsedMs: z.number().positive(),
})
const updatesSchema = z.object({
  animationFrames: z.number().int().nonnegative(),
  changes: z.array(
    z.object({
      frame: z.number().int().nonnegative(),
      seq: z.number().int().nonnegative(),
    })
  ),
})
