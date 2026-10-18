# Report renderers

`ReportRenderer` selects a module-scoped lazy component from `registry.ts`.
The viewer passes authenticated content as a `Blob`, plus the committed title.
Kinds are exact, case-sensitive strings; unknown keys never resolve through an
object prototype. Loading and rendering failures stay within the report pane.
Unknown kinds use `DownloadFallback`: exact original bytes, committed title and
size, sanitized download filename and an owned octet-stream Blob URL.

## Add a kind

Create a component with a default export accepting `ReportRendererProps`:

```tsx
import type { ReportRendererProps } from "../types"

export default function ExampleReport({ content, title }: ReportRendererProps) {
  // Decode and render this kind without interpreting payload bytes as HTML.
  return (
    <p>
      {title}: {content.size} bytes
    </p>
  )
}
```

Add **one entry in `registry.ts`**:

```ts
["example", lazy(() => import("./example/ExampleReport"))],
```

No backend enum, route, database migration or SDK change is required. Publish
`kind: "example"` with the existing report API. Arbitrary kind strings already
travel through the report metadata and index. Keep each renderer behind its own
dynamic import; do not import it eagerly from the viewer or barrel files.

Add sample-payload and malformed-input tests beside the renderer. Run:

```powershell
bun run test src\renderers
bun run typecheck
bunx eslint src\renderers
bun run build
```

`registry.test.tsx` adds an example entry to a copy of the real registry and
renders it through the real dispatcher, without a backend change.

## Untrusted bytes

- Text uses fatal UTF-8 decoding and React text nodes, never `innerHTML`.
- Markdown disables raw HTML. Inline images do not initiate network requests;
  links allow only explicit HTTP(S) URLs or document fragments.
- JSON parses into an expandable tree. Collapsed descendants remain unmounted;
  each container reveals at most 100 additional children at a time.
- Image content must have a recognized raster signature. Only owned Blob URLs
  reach an `<img>`; claimed MIME types cannot enable SVG or HTML.
- Revoke every owned Blob URL on replacement/unmount and ignore late async work.
  Surface decoding failures instead of showing an empty report.

HTML (`html`, `text/html`) and SVG (`svg`, `image/svg+xml`) use the dedicated
`html\HtmlReport.tsx`, never the Markdown or image path. Fatal UTF-8 decoding
feeds `srcDoc` only inside `<iframe sandbox="allow-scripts">`. No same-origin,
forms, popups or top-navigation permissions are granted. A CSP meta element
precedes every untrusted byte, blocking connections, external resources, forms
and base URLs; a report's later meta policy cannot relax the earlier policy.
The frame uses no-referrer and a committed, accessible title. No report messages
are interpreted by the parent.

Raw HTML/SVG content endpoints deliberately return `Content-Disposition:
attachment` while preserving exact bytes, MIME, `nosniff` and restrictive CSP.
An actual browser probe showed why: a top-level CSP-sandboxed document can still
navigate itself to an API URL. CSP sandbox alone is not a navigation firewall.
Downloading direct URLs prevents that document from becoming an executable
top-level Kardia page. Authenticated fetch ignores disposition; the normal
inline viewer still embeds the fetched bytes safely.

`e2e\report-sandbox.spec.ts` checks both kinds using retained filesystem bytes,
actual PostgreSQL metadata and production HTTP. Inline scripts run but cannot
read app cookies/storage/parent DOM, fetch/XHR the API, create popups, submit
forms or navigate the top page, including real user clicks. Raw navigation
downloads the exact original bytes rather than executing them. Browser request
events can describe CSP-blocked attempts: the test verifies CSP failures and no
network response, not a misleading assertion that no attempt was made.

## Tables, charts, diffs and PDF

`table` accepts CSV, an array of JSON records, or an explicit JSON table:

```json
{"columns":["name","duration"],"rows":[["compile",12],["test",null]]}
```

Cells must be strings, finite numbers, booleans or null. Record columns retain
first-seen order, including own keys such as `__proto__`. Rows must match the
header width; at most 128 columns are accepted. `csv` and `text/csv` force CSV
parsing, including headers starting with `[` or `{`. A dedicated module worker
owns parsing, including quoted CSV fields. Filtering and natural column sorting
operate on the parsed rows; the scrollable table mounts at most 24 data rows.
Cells are literal text, never HTML or spreadsheet formulas.

`chart` accepts this JSON contract, with 1–16 aligned series:

```json
{"type":"line","x":[1,2,3],"series":[{"label":"CPU","values":[10,null,30]}]}
```

Line X values must be finite, strictly increasing numbers. `type: "bar"` also
accepts string categories, renders grouped series and includes zero in its Y
scale. Null values represent gaps. A responsive uPlot canvas has a named series
legend and an expandable, sortable data-table alternative.

`diff` accepts only two plain-text strings:

```json
{"original":"before\n","modified":"after\n"}
```

The lazy Monaco diff is read-only on both sides; no language execution, external
links or editable models are enabled. It adapts to narrow screens, follows theme
changes and provides expandable plain-text alternatives. Owned editors and
models are disposed on replacement/unmount.

`pdf` and `application/pdf` accept raw PDF bytes. Lazy PDF.js reads the already
authenticated bytes, not a payload-provided URL. Its worker, CMaps, fonts, WASM
and color profiles are bundled locally under versioned asset paths. One page is
rendered at a time, capped at four million raster pixels and 8,192 pixels per
dimension; previous/next controls and extractable page text remain accessible.
No annotation, form or PDF viewer scripting layer is installed. Loading tasks,
render tasks and page resources are released on replacement/unmount.

Malformed payloads surface explicit report errors. Each heavy renderer and its
workers remain outside the initial application chunk. `e2e\report-heavy.spec.ts`
checks retained reports through actual PostgreSQL, immutable filesystem bytes,
production content HTTP and native browser workers/canvases; desktop-light and
mobile-dark checks include accessibility and page-overflow assertions.

## Large reports

The existing authenticated content request still verifies the complete Blob
against committed byte size before passing it to a renderer. Progressive viewing
does not skip this integrity check or claim progressive network downloads.
The boundary is decimal: reports **over 5,000,000 bytes** use these bounded views:

- CSV parses off the main thread in 131,072-code-unit text chunks. The worker pauses
  after each validated page until the user requests the next; cached prior pages
  remain in the owned worker. Only one page crosses into React and at most 24
  table rows mount. Quoted multiline fields remain intact across chunk boundaries.
- JSON tables validate completely off-thread, then return 1,000-row pages.
- Text uses `useProgressiveContent` to decode 64 KiB byte slices with a streaming,
  fatal UTF-8 decoder, preserving multibyte characters crossing page boundaries.
  Previous/next controls retain exact cached text while only one page mounts.

Large-table sorting/filtering explicitly applies to the current page. The
pagination status distinguishes rows parsed so far from a completed report.
Malformed later pages replace the preview with an error; unfinished CSV is never
described as fully validated. Worker termination and stale-read guards still
apply to replacement and unmount. At or below the boundary, existing whole-table
sorting/filtering and exact row count remain unchanged.

`e2e\report-render-perf.spec.ts` seeds exact 5 MB/10 MB retained payloads, clicks
the actual Reports-tab link and times uncached metadata/content HTTP through
first visible table rows. The Chromium performance gate remains below one
second and 60 fps rounded typical/average scrolling, with p95 frame time at most 20 ms.
All three browser engines exercise page traversal, exact final row totals,
cached backward navigation and bounded large-text rendering. Raw timings and
idle/scroll animation-frame samples are persisted as JSON attachments.
Firefox/WebKit headless cadence is measured, not mislabeled as 60 fps: the local
run measured about 32/22 fps while scrolling, versus about 32 fps idle. These
parity runs do not assert a Chromium-specific frame clock.
