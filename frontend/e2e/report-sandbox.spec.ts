import AxeBuilder from "@axe-core/playwright"
import { expect, test, type Frame, type Request } from "@playwright/test"
import { execFileSync } from "node:child_process"
import { randomUUID } from "node:crypto"
import { readFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { resolve } from "node:path"
import { z } from "zod"
import { seedRendererReport } from "../scripts/seed-renderer-reports"

function hostileReport(kind: "html" | "svg", endpoint: string) {
  const script = `
    const results = { script: true, origin: globalThis.origin, violations: [] };
    const render = () => {
      document.getElementById("sandbox-results").textContent = JSON.stringify(results);
    };
    document.addEventListener("securitypolicyviolation", event => {
      results.violations.push(event.effectiveDirective);
      render();
    });
    const check = (key, action) => {
      try { results[key] = action(); }
      catch (error) { results[key] = error.name; }
    };
    check("cookie", () => document.cookie);
    check("localStorage", () => localStorage.getItem("sandbox-parent"));
    check("sessionStorage", () => sessionStorage.getItem("sandbox-parent"));
    check("parent", () => parent.document.body.getAttribute("data-sandbox-parent"));
    check("popup", () => window.open(${JSON.stringify(endpoint)}) ? "opened" : "blocked");
    Promise.all([
      fetch(${JSON.stringify(endpoint)}, { credentials: "include" })
        .then(() => { results.fetch = "allowed"; }, error => { results.fetch = error.name; }),
      new Promise(resolve => {
        const xhr = new XMLHttpRequest();
        xhr.open("GET", ${JSON.stringify(endpoint)});
        xhr.onload = () => { results.xhr = "allowed"; resolve(); };
        xhr.onerror = () => { results.xhr = "blocked"; resolve(); };
        xhr.send();
      })
    ]).then(render);`
  const controls = `
    <p>Scripts run only inside the isolated report.</p>
    <a id="top-navigation" target="_top" href="${endpoint}" style="display:inline-block;line-height:32px">Try top navigation</a>
    <form method="post" action="${endpoint}" style="display:flex;flex-direction:column;gap:12px;align-items:start;margin-top:12px">
      <input name="probe" value="sandbox" aria-label="Probe value" style="min-height:32px" />
      <button type="submit" style="min-height:32px">Try form submission</button>
    </form>`
  return kind === "html"
    ? `<!doctype html><html lang="en"><head><title>Sandbox probe</title>
       <meta http-equiv="Content-Security-Policy" content="default-src * 'unsafe-inline'"></head>
       <body><h1>Isolated HTML result</h1>${controls}
       <pre id="sandbox-results"></pre><img src="${endpoint}" alt="Blocked request" />
       <script>${script}</script></body></html>`
    : `<svg xmlns="http://www.w3.org/2000/svg" width="320" height="350">
       <title>Isolated SVG result</title>
       <foreignObject width="320" height="350">
         <div xmlns="http://www.w3.org/1999/xhtml">${controls}
           <pre id="sandbox-results" style="white-space:pre-wrap"></pre>
         </div>
       </foreignObject>
       <image href="${endpoint}" width="1" height="1" />
       <script><![CDATA[${script}]]></script></svg>`
}

async function assertProbes(document: Frame) {
  const result = document.locator("#sandbox-results")
  await expect(result).toContainText('"xhr":"blocked"')
  await expect(result).toContainText('"connect-src"')
  const probes = JSON.parse((await result.textContent())!)
  expect(probes).toMatchObject({
    script: true,
    origin: "null",
    localStorage: "SecurityError",
    sessionStorage: "SecurityError",
    fetch: "TypeError",
    xhr: "blocked",
    parent: "SecurityError",
  })
  expect(["SecurityError", ""]).toContain(probes.cookie)
  expect(["blocked", "InvalidAccessError"]).toContain(probes.popup)
}

function assertBlockedRequests(requests: Request[], responses: number[]) {
  expect(responses).toEqual([])
  for (const request of requests)
    expect(request.failure()?.errorText).toMatch(
      /CSP|Content Security Policy|NS_ERROR_DOM_BAD_URI/i
    )
}

for (const kind of ["html", "svg"] as const)
  test(`${kind} scripts cannot escape the opaque report, including a directly opened content URL`, async ({
    page,
    request,
    context,
  }) => {
    test.setTimeout(90_000)
    const fixture = z
      .object({
        directory: z.string().regex(/^wf-ui-[a-zA-Z0-9_-]+$/),
        container: z.string().regex(/^wf-ui-[a-f0-9-]+$/),
      })
      .parse(
        JSON.parse(
          await readFile(
            new URL("../.test-stack.json", import.meta.url),
            "utf8"
          )
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
      .poll(() => sql(`SELECT count(*) FROM runs WHERE id='${runId}'`), {
        timeout: 20_000,
      })
      .toBe("1")
    const marker = randomUUID()
    const origin = new URL(test.info().project.use.baseURL!).origin
    const endpoint = `${origin}/api/v1/runs?sandboxProbe=${marker}`
    const sample = await seedRendererReport(
      resolve(tmpdir(), fixture.directory),
      runId,
      sql,
      {
        kind,
        title: `${kind} adversarial result`,
        bytes: Buffer.from(hostileReport(kind, endpoint)),
      }
    )
    const contentPath = `/api/v1/reports/${sample.id}/content`
    const raw = await request.get(contentPath)
    expect(raw.status()).toBe(200)
    expect(raw.headers()["content-security-policy"]).toContain(
      "sandbox allow-scripts; frame-ancestors 'self'"
    )
    expect(raw.headers()["x-content-type-options"]).toBe("nosniff")
    expect(raw.headers()["x-frame-options"]).toBeUndefined()
    expect(raw.headers()["content-disposition"]).toMatch(/^attachment;/)
    const expectedBytes = Buffer.from(hostileReport(kind, endpoint))
    expect(await raw.body()).toEqual(expectedBytes)
    const forbiddenRequests: Request[] = []
    const forbiddenResponses: number[] = []
    context.on("request", (request) => {
      if (new URL(request.url()).searchParams.get("sandboxProbe") === marker)
        forbiddenRequests.push(request)
    })
    context.on("response", (response) => {
      if (new URL(response.url()).searchParams.get("sandboxProbe") === marker)
        forbiddenResponses.push(response.status())
    })
    await context.addCookies([
      { name: "sandbox-parent", value: "fixture-cookie", url: origin },
    ])
    await page.goto(`/runs/${runId}/reports/${sample.id}`)
    await page.evaluate(() => {
      localStorage.setItem("sandbox-parent", "fixture-local")
      sessionStorage.setItem("sandbox-parent", "fixture-session")
      document.body.dataset.sandboxParent = "fixture-parent"
    })
    const frameElement = page.getByTitle(`${sample.title} (sandboxed report)`)
    await expect(frameElement).toHaveAttribute("sandbox", "allow-scripts")
    const frame = await (await frameElement.elementHandle())!.contentFrame()
    expect(frame).not.toBeNull()
    await assertProbes(frame!)
    const viewerUrl = page.url()
    await frame!.getByRole("button", { name: "Try form submission" }).click()
    await frame!.getByRole("link", { name: "Try top navigation" }).click()
    await page.waitForTimeout(200)
    expect(page.url()).toBe(viewerUrl)
    expect(context.pages()).toHaveLength(1)
    assertBlockedRequests(forbiddenRequests, forbiddenResponses)
    expect(
      await page.evaluate(() => ({
        cookie: document.cookie,
        local: localStorage.getItem("sandbox-parent"),
        session: sessionStorage.getItem("sandbox-parent"),
        parent: document.body.dataset.sandboxParent,
      }))
    ).toEqual({
      cookie: "sandbox-parent=fixture-cookie",
      local: "fixture-local",
      session: "fixture-session",
      parent: "fixture-parent",
    })
    for (const dark of [false, true]) {
      await page.setViewportSize(
        dark ? { width: 375, height: 812 } : { width: 1440, height: 1080 }
      )
      await page.evaluate(async (dark) => {
        document.documentElement.classList.toggle("dark", dark)
        await new Promise<void>((done) => requestAnimationFrame(() => done()))
        await Promise.allSettled(
          document.getAnimations().map((animation) => animation.finished)
        )
      }, dark)
      const axe = await new AxeBuilder({ page })
        .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
        .analyze()
      expect(
        axe.violations.filter(
          (issue) => issue.impact === "critical" || issue.impact === "serious"
        )
      ).toEqual([])
      expect(
        await page.evaluate(
          () => document.documentElement.scrollWidth <= innerWidth
        )
      ).toBe(true)
    }
    const direct = await context.newPage()
    try {
      await direct.goto("/")
      const before = direct.url()
      const download = direct.waitForEvent("download")
      await direct.evaluate((url) => location.assign(url), contentPath)
      const received = await download
      const path = await received.path()
      expect(path).not.toBeNull()
      expect(await readFile(path!)).toEqual(expectedBytes)
      expect(received.suggestedFilename()).toBe(`report-${sample.id}.${kind}`)
      expect(direct.url()).toBe(before)
      await expect(direct.locator("#sandbox-results")).toHaveCount(0)
      assertBlockedRequests(forbiddenRequests, forbiddenResponses)
    } finally {
      await direct.close()
    }
  })
