import { createHash, randomUUID } from "node:crypto"
import { mkdir, writeFile } from "node:fs/promises"
import { resolve } from "node:path"

export async function seedRendererReports(
  storage: string,
  runId: string,
  sql: (statement: string) => string
) {
  const samples = [
    {
      kind: "markdown",
      title: "Release summary",
      bytes: Buffer.from(
        '# Report result\n\n**Passed**\n\n- build\n- verify\n\n<script>window.reportScriptRan=true</script>\n\n<img src="/api/v1/private" onerror="window.reportScriptRan=true">\n\n![Tracking](https://example.invalid/pixel.png)\n\n[Unsafe](/api/v1/runs)\n\n[Documentation](https://example.com/result)'
      ),
    },
    {
      kind: "text",
      title: "Build output",
      bytes: Buffer.from('first\n  second <script>alert("x")</script> \u03bb'),
    },
    {
      kind: "json",
      title: "Metrics tree",
      bytes: Buffer.from(
        '{"__proto__":{"nested":true},"constructor":"safe","values":[null,false,42,"<script>\\n"]}'
      ),
    },
    {
      kind: "image",
      title: "Raster screenshot",
      bytes: Buffer.from(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=",
        "base64"
      ),
    },
    {
      kind: "json",
      title: "Invalid JSON",
      bytes: Buffer.from('{"incomplete":'),
    },
    {
      kind: "image",
      title: "Rejected SVG",
      bytes: Buffer.from('<svg onload="window.reportScriptRan=true"></svg>'),
    },
    {
      kind: "image",
      title: "Truncated PNG",
      bytes: Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    },
    {
      kind: "unregistered",
      title: "Unknown report",
      bytes: Buffer.from("<script>window.reportScriptRan=true</script>"),
    },
  ]
  const items = []
  for (const sample of samples)
    items.push(await seedRendererReport(storage, runId, sql, sample))
  return items
}

export async function seedRendererReport(
  storage: string,
  runId: string,
  sql: (statement: string) => string,
  sample: {
    kind: string
    title: string
    bytes: Buffer
    sourceKey?: string
    tags?: string[]
  }
) {
  const directory = resolve(storage, "reports", runId)
  await mkdir(directory, { recursive: true })
  const quote = (value: string) => `'${value.replaceAll("'", "''")}'`
  const source = quote(sample.sourceKey ?? "$post/report-renderer")
  const timestamp = Date.now().toString(16).padStart(12, "0")
  const id = `${timestamp.slice(0, 8)}-${timestamp.slice(8)}-7${randomUUID().slice(15)}`
  await writeFile(resolve(directory, id), sample.bytes, { flag: "wx" })
  const hash = createHash("sha256").update(sample.bytes).digest("hex")
  const tags = (sample.tags ?? ["renderer", "review"]).map(quote).join(",")
  sql(`BEGIN;
    INSERT INTO task_attempts(run_day,run_id,task_key,attempt,status,deadline_at,queued_at,started_at,ended_at)
    VALUES(uuid_v7_run_day('${runId}'),'${runId}',${source},1,5,
      clock_timestamp()+interval '1 hour',clock_timestamp(),clock_timestamp(),clock_timestamp())
    ON CONFLICT DO NOTHING;
    INSERT INTO report_ids(id,run_day) VALUES('${id}',uuid_v7_run_day('${runId}'));
    INSERT INTO reports(run_day,id,run_id,source_key,attempt,kind,title,tags,size_bytes,blob_uri,sha256,created_at)
    VALUES(uuid_v7_run_day('${runId}'),'${id}','${runId}',${source},1,
      ${quote(sample.kind)},${quote(sample.title)},ARRAY[${tags}]::text[],${sample.bytes.length},
      'reports/${runId}/${id}',decode('${hash}','hex'),clock_timestamp());
    COMMIT;`)
  return {
    id,
    kind: sample.kind,
    title: sample.title,
    sizeBytes: sample.bytes.length,
  }
}
