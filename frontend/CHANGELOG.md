# Changelog

All notable frontend changes are documented here, using Keep a Changelog.

## [Unreleased]

### Added
- Bounded progressive report viewing above decimal 5 MB: backpressured CSV
  worker pages, validated JSON-table pages and exact UTF-8 text pages, with
  previous/next controls and persisted real-browser performance measurements.
- Lazy worker-parsed, sortable/virtual CSV/JSON tables, uPlot line/grouped-bar
  charts, read-only Monaco diffs and paginated PDF.js reports with accessible
  text/data alternatives, owned cleanup and versioned local PDF dependencies.
- Sandboxed HTML/SVG reports with restrictive CSP and safe direct-URL downloads.
- Side-by-side comparison of exact reports across any two paginated series runs.
- Live run Reports tab with stage/node/task grouping, exact kind/tag filters,
  authorized metadata refresh and separate reconnect-safe ReportAdded handling.
- Unknown report kinds download exact bytes as owned binary Blob URLs with
  committed title/size, safe filenames and replacement/unmount cleanup.
- Renderer-only extension guide and a single-entry lazy registration check.
- Lazy report renderers for safe Markdown, text, expandable JSON and raster
  images; Markdown uses pinned `react-markdown` 10.1.0 without raw HTML plugins.
- Shared MessagePack live connection, reference-counted scope subscriptions and
  animation-frame-batched external stores using the official SignalR clients.
- React/TypeScript SPA foundation with typed file routes, server-state queries,
  system-aware light/dark themes and an accessible schedule-management form.
- Separate generated public/internal API clients with live contract drift checks,
  runtime response validation and strong ETag concurrency protection.
- Lossless native JSON number handling for Int64 variable defaults and overrides.
- Delegated Entra authentication and an explicit loopback-development guard.
- Real PostgreSQL/HTTP CRUD browser tests on Chromium, Firefox and WebKit, axe
  accessibility checks, draft/authentication units and a frontend CI lane.
