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
