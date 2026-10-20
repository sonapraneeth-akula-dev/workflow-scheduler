import AxeBuilder from "@axe-core/playwright"
import {
  expect,
  test,
  type APIRequestContext,
  type Page,
} from "@playwright/test"
import { execFileSync } from "node:child_process"
import { readFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { resolve } from "node:path"
import { z } from "zod"
import { seedRendererReport } from "../scripts/seed-renderer-reports"

function pdfFixture() {
  const objects = [
    "<< /Type /Catalog /Pages 2 0 R >>",
    "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
    "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Resources << /Font << /F1 5 0 R >> >> /Contents 6 0 R >>",
    "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Resources << /Font << /F1 5 0 R >> >> /Contents 7 0 R >>",
    "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    ...["First PDF page", "Second PDF page"].map((text) => {
      const stream = `BT /F1 18 Tf 30 350 Td (${text}) Tj ET`
      return `<< /Length ${stream.length} >>\nstream\n${stream}\nendstream`
    }),
  ]
  let data = "%PDF-1.4\n"
  const offsets = [0]
  for (const [index, object] of objects.entries()) {
    offsets.push(Buffer.byteLength(data))
    data += `${index + 1} 0 obj\n${object}\nendobj\n`
  }
  const start = Buffer.byteLength(data)
  data += `xref\n0 ${offsets.length}\n0000000000 65535 f \n`
  for (const offset of offsets.slice(1))
    data += `${String(offset).padStart(10, "0")} 00000 n \n`
  return Buffer.from(
    `${data}trailer\n<< /Size ${offsets.length} /Root 1 0 R >>\nstartxref\n${start}\n%%EOF\n`
  )
}

async function retainedRun(request: APIRequestContext) {
  const fixture = z
    .object({
      directory: z.string().regex(/^wf-ui-[a-zA-Z0-9_-]+$/),
      container: z.string().regex(/^wf-ui-[a-f0-9-]+$/),
    })
    .parse(
      JSON.parse(
        await readFile(new URL("../.test-stack.json", import.meta.url), "utf8")
      )
    )
  const created = await request.post("/api/v1/runs", {
    data: { workflowId: "report-tests/viewer/basic", variables: {} },
  })
  expect(created.status()).toBe(202)
  const { runId } = z.object({ runId: z.uuid() }).parse(await created.json())
  const sql = (statement: string) =>
    execFileSync(
      "docker",
      [
        "exec",
        fixture.container,
        "psql",
        "-U",
        "workflow",
        "-d",
        "workflow",
        "-At",
        "-v",
        "ON_ERROR_STOP=1",
        "-c",
        statement,
      ],
      { encoding: "utf8", timeout: 20_000 }
    ).trim()
  await expect
    .poll(() => sql(`SELECT count(*) FROM runs WHERE id='${runId}'`))
    .toBe("1")
  return {
    runId,
    seed: (kind: string, title: string, bytes: Buffer) =>
      seedRendererReport(resolve(tmpdir(), fixture.directory), runId, sql, {
        kind,
        title,
        bytes,
      }),
  }
}

async function accessibleThemes(page: Page) {
  for (const dark of [false, true]) {
    await page.setViewportSize(
      dark ? { width: 375, height: 812 } : { width: 1440, height: 1080 }
    )
    await page.evaluate(async (dark) => {
      document.documentElement.classList.toggle("dark", dark)
      await new Promise<void>((resolve) =>
        requestAnimationFrame(() => resolve())
      )
      await Promise.all(document.getAnimations().map((item) => item.finished))
    }, dark)
    const axe = await new AxeBuilder({ page })
      .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
      .analyze()
    expect(
      axe.violations.filter(
        (item) => item.impact === "critical" || item.impact === "serious"
      )
    ).toEqual([])
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth
      )
    ).toBe(true)
  }
}

test("retained table, grouped/line chart, read-only diff and multi-page PDF use native workers and accessible responsive views", async ({
  page,
  request,
}) => {
  test.setTimeout(240_000)
  const fixture = await retainedRun(request)
  const workers: string[] = []
  page.on("worker", (worker) => workers.push(worker.url()))
  const csv = await fixture.seed(
    "csv",
    "Virtual CSV",
    Buffer.from(
      `name,count\n${Array.from({ length: 1000 }, (_, i) => `item-${i},${1000 - i}`).join("\n")}`
    )
  )
  await page.goto(`/runs/${fixture.runId}/reports/${csv.id}`)
  const table = page.getByRole("table")
  await expect(table).toHaveAttribute("aria-rowcount", "1001")
  await expect(table.locator("tbody tr[aria-rowindex]")).toHaveCount(24)
  await page
    .getByRole("region", { name: "Virtual CSV scrollable data" })
    .evaluate((node) => {
      node.scrollTop = 32 * 500
    })
  await expect(
    page.getByRole("cell", { name: "item-500", exact: true })
  ).toBeVisible()
  await page.getByRole("searchbox").fill("item-999")
  await expect(
    page.getByRole("cell", { name: "item-999", exact: true })
  ).toBeVisible()
  await page.getByRole("searchbox").fill("item-99")
  await page.getByRole("combobox").selectOption("1")
  await expect(
    table.locator("tbody tr[aria-rowindex] td:nth-child(2)").first()
  ).toHaveText("1")
  await page.getByRole("button", { name: "Ascending" }).click()
  await expect(
    table.locator("tbody tr[aria-rowindex] td:nth-child(2)").first()
  ).toHaveText("901")
  await accessibleThemes(page)
  expect(workers.some((url) => url.includes("table.worker"))).toBe(true)

  const records = await fixture.seed(
    "table",
    "JSON table",
    Buffer.from(
      '[{"__proto__":"<script>literal</script>","value":null},{"value":true}]'
    )
  )
  await page.goto(`/runs/${fixture.runId}/reports/${records.id}`)
  await expect(
    page.getByRole("columnheader", { name: "__proto__" })
  ).toBeVisible()
  await expect(
    page.getByRole("cell", { name: "<script>literal</script>" })
  ).toBeVisible()
  await expect(page.locator("main script")).toHaveCount(0)
  await accessibleThemes(page)

  for (const type of ["line", "bar"] as const) {
    const report = await fixture.seed(
      "chart",
      `${type} results`,
      Buffer.from(
        JSON.stringify({
          type,
          xLabel: "Sample",
          yLabel: "Measurement",
          x: type === "line" ? [1, 2, 3] : ["first", "second", "third"],
          series: [
            { label: "CPU", values: [10, 20, 30] },
            { label: "RAM", values: [20, null, 10] },
          ],
        })
      )
    )
    await page.goto(`/runs/${fixture.runId}/reports/${report.id}`)
    await expect(
      page.getByRole("img", { name: `${type} results: ${type} chart` })
    ).toBeVisible()
    const canvas = page.locator(".uplot canvas").first()
    await expect(canvas).toBeVisible()
    expect(
      await canvas.evaluate((node) => {
        if (!(node instanceof HTMLCanvasElement))
          throw new Error("Missing chart canvas.")
        return node
          .getContext("2d")!
          .getImageData(0, 0, node.width, node.height)
          .data.some((value, index) => index % 4 === 3 && value > 0)
      })
    ).toBe(true)
    const chart = page.getByRole("region", {
      name: `${type} results chart`,
      exact: true,
    })
    await expect(
      chart.getByText(`${type} results`, { exact: true })
    ).toBeVisible()
    await expect(
      chart.getByText("X axis: Sample; Y axis: Measurement.")
    ).toBeAttached()
    const gridOn = await canvas.evaluate((node: HTMLCanvasElement) =>
      node.toDataURL()
    )
    const grid = chart.getByRole("button", { name: "Gridlines", exact: true })
    await grid.click()
    await expect(grid).toHaveAttribute("aria-pressed", "false")
    await expect
      .poll(() =>
        canvas.evaluate((node: HTMLCanvasElement) => node.toDataURL())
      )
      .not.toBe(gridOn)
    await grid.click()
    await expect(grid).toHaveAttribute("aria-pressed", "true")
    const overlay = chart.locator(".u-over")
    const bounds = await overlay.boundingBox()
    await overlay.hover({
      position: { x: bounds!.width / 2, y: bounds!.height / 2 },
    })
    await expect(chart.getByRole("tooltip")).toContainText(
      type === "line" ? "Sample: 2" : "Sample: second"
    )
    await expect(chart.getByRole("tooltip")).toContainText("CPU 20")
    await expect(chart.getByRole("tooltip")).toContainText("RAM Unavailable")
    await page.keyboard.press("Escape")
    await expect(chart.getByRole("tooltip")).toHaveCount(0)
    const plot = chart.getByRole("img")
    await plot.focus()
    await page.keyboard.press("Home")
    await expect(chart.getByRole("tooltip")).toContainText(
      type === "line" ? "Sample: 1" : "Sample: first"
    )
    await page.keyboard.press("End")
    await expect(chart.getByRole("tooltip")).toContainText(
      type === "line" ? "Sample: 3" : "Sample: third"
    )
    const download = page.waitForEvent("download")
    await chart
      .getByRole("button", {
        name: `Export ${type} results as PNG`,
        exact: true,
      })
      .click()
    const image = await download
    expect(image.suggestedFilename()).toBe(`${type} results.png`)
    const bytes = await readFile((await image.path())!)
    expect(bytes.subarray(0, 8).toString("hex")).toBe("89504e470d0a1a0a")
    expect(bytes.readUInt32BE(16)).toBeGreaterThan(0)
    expect(bytes.readUInt32BE(20)).toBeGreaterThan(360)
    expect(bytes.length).toBeGreaterThan(1000)
    await page.getByText("Chart data table", { exact: true }).click()
    await expect(page.getByRole("table")).toHaveAttribute("aria-rowcount", "4")
    await expect(page.getByRole("columnheader", { name: "CPU" })).toBeVisible()
    await accessibleThemes(page)
  }

  const diff = await fixture.seed(
    "diff",
    "Code changes",
    Buffer.from(
      JSON.stringify({
        original: "before\n<script>literal</script>",
        modified: "after\nUnicode \u03bb",
      })
    )
  )
  await page.goto(`/runs/${fixture.runId}/reports/${diff.id}`)
  await expect(page.locator(".monaco-diff-editor")).toBeVisible()
  const modified = page.getByRole("textbox", {
    name: "Code changes: modified text",
  })
  await expect(modified).toHaveJSProperty("readOnly", true)
  await modified.focus()
  await page.keyboard.type("not editable")
  await page.getByText("Plain-text diff alternatives").click()
  await expect(page.locator("pre").filter({ hasText: "after" })).toHaveText(
    "after\nUnicode \u03bb"
  )
  await expect(page.locator("pre").filter({ hasText: "before" })).toHaveText(
    "before\n<script>literal</script>"
  )
  await accessibleThemes(page)
  expect(workers.some((url) => url.includes("editor.worker"))).toBe(true)

  const pdf = await fixture.seed(
    "application/pdf",
    "Document results",
    pdfFixture()
  )
  await page.goto(`/runs/${fixture.runId}/reports/${pdf.id}`)
  const first = page.getByRole("img", { name: "Document results: page 1 of 2" })
  await expect(first).toBeVisible()
  await expect(
    page.getByRole("button", { name: "Previous PDF page" })
  ).toBeDisabled()
  await page.getByText("PDF page text", { exact: true }).click()
  await expect(page.getByText("First PDF page", { exact: true })).toBeVisible()
  await page.getByRole("button", { name: "Next PDF page" }).click()
  await expect(
    page.getByRole("img", { name: "Document results: page 2 of 2" })
  ).toBeVisible()
  await expect(page.getByText("First PDF page", { exact: true })).toHaveCount(0)
  await page.getByText("PDF page text", { exact: true }).click()
  await expect(page.getByText("Second PDF page", { exact: true })).toBeVisible()
  await expect(
    page.getByRole("button", { name: "Next PDF page" })
  ).toBeDisabled()
  await accessibleThemes(page)
  expect(workers.some((url) => url.includes("pdf.worker"))).toBe(true)
})

test("malformed retained heavy reports fail explicitly without poisoning the next report", async ({
  page,
  request,
}) => {
  test.setTimeout(90_000)
  const fixture = await retainedRun(request)
  for (const [kind, bytes, error] of [
    ["csv", Buffer.from('a,b\n"unclosed'), "Invalid CSV report"],
    [
      "table",
      Buffer.from('{"columns":["a"],"rows":[["one","extra"]]}'),
      "Table rows must match",
    ],
    [
      "chart",
      Buffer.from(
        '{"type":"line","x":[2,1],"series":[{"label":"x","values":[1,2]}]}'
      ),
      "strictly increase",
    ],
    [
      "diff",
      Buffer.from('{"original":42,"modified":"x"}'),
      "original and modified text strings",
    ],
    ["pdf", Buffer.from("not a PDF"), "Invalid PDF"],
    ["csv", Buffer.from([0xff, 0xfe]), "not valid UTF-8"],
  ] as const) {
    const report = await fixture.seed(kind, `Malformed ${kind}`, bytes)
    await page.goto(`/runs/${fixture.runId}/reports/${report.id}`)
    await expect(page.getByRole("alert")).toContainText(error)
    await expect(
      page.getByRole("heading", { name: report.title, exact: true })
    ).toBeVisible()
  }
  const recovered = await fixture.seed(
    "chart",
    "Recovered chart",
    Buffer.from(
      '{"type":"bar","x":["one"],"series":[{"label":"value","values":[1]}]}'
    )
  )
  await page.goto(`/runs/${fixture.runId}/reports/${recovered.id}`)
  await expect(page.locator(".uplot canvas").first()).toBeVisible()
  await expect(page.getByRole("alert")).toHaveCount(0)
})
