# Changelog

All notable frontend changes are documented here, using Keep a Changelog.

## [Unreleased]

### Added
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
