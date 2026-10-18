# Kardia frontend

React 19, TypeScript 5, Vite, Bun, TanStack Router/Query, Tailwind CSS 4 and owned
shadcn/Radix primitives. File routes are generated into `src\routeTree.gen.ts`;
commit generated route changes with their source routes.

## Timestamp display

Application instants remain UTC in storage and API/live data. UI timestamps use
the browser's local timezone, 24-hour time and exactly three fractional digits,
for example `18 Oct 2026, 16:11:48.146 IST`. Timezone labels follow the instant's
daylight-saving rules (`PST` in winter, `PDT` in summer). Native English short
names are preferred; a full timezone name is used when no abbreviation exists,
never an invented abbreviation or an unlabeled numeric offset.
Original RFC3339 values, including sub-millisecond precision, remain unchanged
in `time[datetime]` and hover titles. Formatting never changes sorting, cursors,
durations, filters or downloaded content. UTC daily aggregation buckets and
explicit UTC date-filter boundaries remain labeled UTC; they are dates, not
localized instants. Cron evaluation still uses the configured schedule timezone.

## Application shell

The Kardia wordmark is an independent home link at the viewport's padded left
edge, using Bahnschrift with local Segoe fallbacks rather than navigation text.
Primary navigation stays geometrically centered between equal-width desktop
side columns. On smaller screens it occupies its own centered second row,
without overlapping the brand or account actions.
Main content fills the remaining viewport width with matching 20 px mobile /
32 px desktop horizontal padding, rather than a narrow fixed maximum width.
Headings use Segoe UI Variable Display; body and controls use Segoe UI Variable
Text. Code, identifiers and the diff editor use Cascadia Code. Fonts are local,
with Segoe UI/system and Cascadia Mono/Consolas/monospace fallbacks; no font
downloads are required. The brand retains its distinct wordmark face.
Light and dark themes share blue primary actions, teal interaction accents and
distinct success, warning, error and active-status colors. Status labels and
icons remain readable without color. All application surfaces, including
avatars, badges, controls, popovers and dialogs, have square corners; embedded
report documents retain their own sandboxed content styling.
The right-hand theme action is an accessible sun/moon icon. It follows the
system theme until explicitly changed, persists the choice in local storage,
and surfaces unavailable/corrupt preference storage rather than hiding errors.
After the theme action, the account menu shows the signed-in name and an
initials avatar (the current sign-in supplies no photo). Profile details use
fresh server permission checks for system, team and type administrator access,
including inherited roles; no role is inferred from display names or decoded
tokens. Loading/errors hide old role details. Settings offers light, dark and
system appearance. A divider separates the red sign-out action, which reuses
the existing Entra logout and live-subscription cleanup. Explicit Development
authentication has no interactive session: sign-out is disabled with an
explanation rather than pretending to log out the fixed local identity.

## Run locally

Requires Bun 1.4.2, Node 24 and the running control plane. Start the backend with
`--dev` in the Development environment, using the backend README's PostgreSQL
and upload-signing-key configuration. Register a workflow before scheduling it.

```powershell
Set-Location frontend
bun install --frozen-lockfile
$env:VITE_AUTH_MODE = "development"
$env:KARDIA_API_URL = "http://127.0.0.1:5001"
bun run dev --host 127.0.0.1
```

Open the printed Vite address, then Create schedule. Supply the exact
`team/type/workflow` identity, name, IANA timezone and either five-field cron or
positive whole-second interval. Variables are a JSON object of overrides;
defaults and schema validation come from the selected workflow version.

Workflow history and immutable Task Plan pages expose **Trigger run** to
Operators. The native modal reads a fresh, Operator-authorized variable schema,
then captures its exact immutable version UUID. Defaults prefill the shared
`VariablesForm`; strings/enums, Int32/Int64, finite doubles, booleans, JSON string
lists, constant-format durations and name-only secret references validate inline.
Optional overrides can be unset (the workflow default still applies). Required
means supplied, not nonempty: an explicitly edited empty string remains valid.
Secret literals are never requested or resolved by the form. Invalid fields or
requests exceeding 1 MiB disable submission. Viewer task-plan responses still
exclude defaults and variable values.

Manual acceptance uses HTTP 202 and an idempotency key without automatic retries.
After an uncertain response the exact payload, pinned UUID and key remain frozen,
including across modal close/reopen; Retry cannot silently create another run.
Definite validation/authorization/not-found rejections permit correction. A valid
receipt links the asynchronously created run; **Prepare another run** explicitly
starts a new request. Closing/reopening retains the captured version; reload the
page to capture a newly active version. Schedule state is never modified.

Large JSON numbers, including both Int64 limits, retain their exact source
tokens through preview, create/edit, response validation and reload. Native raw
JSON serialization avoids introducing a numeric wrapper dependency. Browsers
without the required native support fail explicitly before unsafe edits.

The server validates the complete draft and computes five upcoming UTC instants.
The UI shows them in the browser's local timezone with millisecond precision and
DST-aware timezone names. The selected schedule timezone still controls cron
evaluation, not presentation. Invalid fields, failed/offline previews and
pending mutations disable submission. Editing uses the response's strong ETag
in `If-Match`; a real 412 preserves unsaved edits and requires an explicit reload.
Pause/resume preserve
unsaved edits; reload intentionally discards them. Delete requires confirmation
and retains historical runs/series.

`/schedules/<uuid>` reloads a persisted schedule directly. Code-origin schedules
show a Code badge, applied file path and commit; UI-created schedules show a UI
badge. Code views have no variable-edit form or edit/delete/pause/resume controls,
and the actual API rejects all four mutations with 409. Legacy missing provenance
is explicit rather than fabricated. The shared `usePermissions` hook and `Can` component hide
unpermitted actions using authenticated `/api/v1/permissions/<scope>?id=...`
reads, never token-decoded role guesses. Author and Operator remain independent;
schedule mutations require Operator. Permission reads reuse server resource
ownership and role checks, fail closed during loading, errors and refresh, and
refresh every 30 seconds and on window focus. Forms stay mounted during these
checks, preserving unsaved drafts. Mutation endpoints remain authoritative,
including after a role is revoked.

`/machines` and `/machines/<uuid>` reuse the existing global Admin permission
scope and public inventory/PATCH API. The list shows OS/architecture, labels,
pool, health, hardware/task-slot capacity and reservations; detail adds current
CPU/RAM/disk, heartbeat and agent version. Pool/OS/health and AND label filters
survive URL reload. The label/pool modal validates exact server bounds, sends
only changed fields, and preserves inputs on validation/write errors. Clearing
labels requires an explicit empty map. Existing reservations stay visible while
physical release acknowledgement is pending, including after lease expiry.

The existing `machines/*` MessagePack scope receives `Resync` from committed
agent/reservation changes, coalesced at 250 ms. It invalidates the shared list
cache; reconnect resynchronizes and manual refresh retries the transport.
Inventory has no timer polling. Authorization/read/live errors hide cached
metadata and edit controls; reconnecting transport marks existing rows stale.
No additional sign-in/logout flow is introduced.

For actual PostgreSQL, signed-JWT and Chromium/Firefox/WebKit acceptance:

```powershell
$env:WF_MACHINE_BROWSER = "true"
$env:WF_FRONTEND_ROOT = (Resolve-Path frontend).Path
dotnet test backend\tests\Wf.Integration.Tests --filter "FullyQualifiedName~Machines_browser_real"
```

`scripts\machines-browser.ts` starts an isolated ephemeral Vite proxy.
`e2e\machines.spec.ts` verifies mixed registered Windows/Linux inventory, a
real release-pending reservation, filtered reload, audited metadata replacement
and clearing, cross-page live invalidation, production registry heartbeats,
Viewer denial, cached-data hiding on real 403, mobile/dark and dialog
accessibility. JWT validation and HTTP/MessagePack responses are not mocked;
the heartbeat-driving route exists only in the integration fixture.

Operator-only Run now is available on schedule details (including Code and
paused schedules) and series history. Series history uses the API's authoritative
nullable current schedule identity, including when filters return no rows;
deleted/legacy unlinked series never guess an endpoint. The request sends no
variable/version/series overrides and leaves the saved schedule, next fire and
fire history unchanged. Unsaved schedule drafts remain mounted.

Each deliberate run uses an idempotency key. Pending writes disable submission;
there are no automatic retries. After a failed or lost response, Retry Run now
reuses that key; only a successful acceptance permits a new key. HTTP 202 with a
valid run ID is required. The Manual receipt links to the accepted run and
explicitly distinguishes acceptance from asynchronous engine creation. Refresh
run history to read the committed row in the same permanent series.

Home lists permitted teams; `/t/<team>` shows type descriptions and latest-run
status counts, and `/t/<team>/<type>` shows workflows with independent active
and latest versions. Home to workflow list takes two clicks. Never-run workflows
are explicit; pages load 50 workflows with manual continuation. Authorized
type-scoped MessagePack updates refresh the affected workflow list and team
counts without timer polling or requiring a broader team grant. Failed live
connections expose retry and manual refresh; denied reads hide previously cached
rows. Each team's optional presentation controls type ordering, metadata row
groups, extra metadata columns, default filters and labels. Unlisted types keep
server order; missing group/column values show `Not set`. Presentation caches
are team-scoped; type-only grants receive no hidden team-wide presentation.
Defaults apply on the server before workflow paging and type status counts
through `defaults=true`, without copying stored keys/values into URLs or
applying manual-filter size limits to valid stored configuration. Explicit
`meta.<key>` filters override corresponding defaults. Workflow/series history
and Runs search are available through their own links.

Catalogue and scheduled-fire run IDs link to `/runs/<uuid>`. The run's Task Plan
link opens `/t/<team>/<type>/w/<workflow>/versions/<version UUID>` using the pinned
run version, including after a newer publish. Append `/tasks/<encoded node/task>`
for a specific declared task; unknown task keys are explicit. Task Plan is
read-only and renders manifest node/task order, dependency conditions, lifecycle
stages, effective planned placement, retry budgets/categories/backoff and
run/node/attempt/request timeouts. Placement variables remain unresolved
references, not inferred selected agents.

SDK function names describe the actual declared delegate; they do not predict
commands generated by its code. Command executables and sanitized HTTP actions
are declared without arguments, environment, credentials, query/fragment,
headers or bodies. Variable values/defaults, input bindings and package/source
capabilities never enter the plan response. Legacy missing declarations are
explicit. Fresh server Viewer checks apply to every immutable-version request;
failed/refused refreshes hide a previously cached plan. `e2e/task-plan.spec.ts`
publishes two real versions, triggers a pinned run, follows its historical link,
checks safe response/UI contents, selected-task reload and light/dark mobile
accessibility on Chromium, Firefox and WebKit.

Workflow lists and workflow history link to **Versions** at
`/t/<team>/<type>/w/<workflow>/versions`. The uncached history reads 50
registrations per page, with immutable UUID/SemVer links, Git author/commit,
registering identity, nullable notes, SDK and local registration time. Active and
latest pointers remain distinct after rollback. Version details retain the
read-only Task Plan, show registration provenance and default comparison from
the pinned UUID, even when that version is outside the loaded history page.

**Compare versions** explicitly fetches the selected immutable pair. Added,
removed and changed manifest declarations have expandable before/after text;
Int64 defaults retain exact JSON tokens. The existing lazy, read-only Monaco
renderer loads only for a requested comparison, with exact retained script
texts, responsive layout, theme changes and plain-text alternatives. This
explicit source view includes authored literals, unlike the safe Task Plan;
secret references are never resolved. Failed/refused refreshes hide stale
history or comparison data and expose retry. Real browser tests publish
versions, traverse keyset pages across a new registration, follow historical
run links, corrupt and restore an owned source blob, and check light/dark mobile
accessibility on Chromium, Firefox and WebKit.

The run page renders the pinned DAG as cached ordered node positions and SVG
dependency paths. Stable memoized edges and node buttons avoid layout recomputation
on live updates; the production RunStore supplies one immutable update per animation
frame. Selecting a node links its declared tasks to the same historical Task Plan.
Unknown nodes, invalid states and mismatched pinned identities fail explicitly.

`/runs/<run UUID>/reports/<report UUID>` opens a committed report directly.
The viewer first checks the run's authorized report index, then fetches the
selected report's raw bytes through the same authenticated API client. Kind,
title, source, attempt and size come from stored metadata, never URL parameters;
foreign/missing reports and changed content sizes fail explicitly.
Markdown, text, expandable JSON and raster images load through independent lazy
renderer modules. Markdown never renders raw HTML or fetches inline images.
JSON children mount only when expanded, in batches of 100. Image headers must
identify a supported raster format; SVG/HTML cannot enter the image path.
Refresh failures hide previously displayed content. Renderer and decoding
failures stay within the report pane.
HTML/SVG use a separate opaque-origin, scripts-only sandbox iframe. An enforced
CSP precedes untrusted markup, blocking API connections/external resources/forms;
same-origin access, popups and top navigation remain disallowed. Direct raw
HTML/SVG URLs download instead of executing as top-level pages: browser sandbox
alone does not prohibit a top-level document from navigating itself. Normal
authenticated fetch and inline rendering remain available. The adversarial
`e2e/report-sandbox.spec.ts` checks actual scripts, API-blocked attempts, storage,
parent DOM, clicked forms/navigation and exact direct-download bytes.
Unknown kinds show a download link with the committed title and exact byte size.
Downloads use owned octet-stream Blob URLs, sanitize only the filename (not the
visible title), and revoke URLs on replacement/unmount; they do not navigate to
or execute unknown HTML/SVG payloads.
Adding a renderer requires one lazy entry in `src\renderers\registry.ts`;
`src\renderers\README.md` documents the component contract, extension test and
untrusted-content rules. No backend kind enum or migration is required.

The run's Reports tab groups the exact committed stage/node/task source and
shows each report's title, kind, tags, attempt, byte size and publication time.
Kind and tag filters use exact, case-sensitive AND matches over the authorized
metadata index; changing filters needs no additional request. Empty runs and
empty filtered results have distinct messages. Report links open the same
authenticated viewer and therefore support unknown-kind downloads too.

Reports and the mounted graph share one reference-counted run subscription.
Separate MessagePack `ReportAdded` IDs advance only the report refresh version,
never the run lifecycle sequence. Joined/reconnected/resynced scopes and report
notifications restart even an in-flight stale index read, then refetch durable
metadata. Run snapshots/events are not reloaded just because a report arrives.
There is no timer polling. Denied or failed REST/live refreshes hide cached
reports and expose explicit refresh/connection retry controls.

`e2e/report-index.spec.ts` creates a separate real run per browser, seeds retained
report rows/blobs, and measures a subsequent fixture blob write + PostgreSQL
commit + actual notification/MessagePack + visible insertion at <=1 second.
The browser fixture checks exact filters, all source grouping levels, one shared
WebSocket, no periodic reads, real viewer navigation and two-theme accessibility.
It does not represent SQL fixture insertion as SDK publishing; separate backend
tests exercise the production publishing endpoint.

Series history links to `/series/<series UUID>/compare`. Load more series runs
continues the complete run history, not just the newest 50. Choose any distinct
left/right pair and a committed report from the left run. Comparison matches
its exact source key and title, preserving case, whitespace and Unicode.
The authenticated comparison endpoint checks all three resource scopes;
both returned metadata identities are validated before fetching raw content.
Each side reuses its own kind renderer, even if kinds or attempts differ.
Missing matches (404) and ambiguous matches (409) remain explicit failures:
no fabricated empty side or arbitrary latest attempt. Changing either run or
report clears the old comparison; failed refreshes hide both cached panes.

`e2e/report-compare.spec.ts` creates three real manually triggered runs in a
new series per browser and seeds retained report bytes/metadata. Chromium,
Firefox and WebKit exercise different run pairs, independent text/Markdown
renderers, exact whitespace/Unicode, wrong-source absence, ambiguous duplicate
matches, refresh recovery and direct-route reload. Desktop-light and mobile-dark
views have no critical/serious axe violations or document overflow. Fixture
insertion does not claim SDK publishing; requests use the actual comparison,
history, index and raw-content endpoints.

`e2e/report-renderers.spec.ts` reads real HTTP content from immutable filesystem
objects and PostgreSQL report/attempt rows in the isolated UI fixture. Fixture
rows model retained published reports; this browser fixture does not claim to
exercise SDK publishing, which has separate backend integration coverage.
Chromium, Firefox and WebKit check safe Markdown, exact text, keyboard JSON
expansion, real image decoding, explicit malformed-payload errors and desktop
light/mobile dark accessibility without API transport mocks.

Run details group workflow/run identity, committed sequence, status, controls
and related history/Task Plan links in a compact responsive summary. The graph
follows immediately below its tabs; concise keyboard hints keep the full
instructions available to assistive technology. Selecting a node opens its
inspector beside the graph on wide screens and below it on smaller screens.
Status, reconnect/permission failures, placement, queue and lineage information
remain visible rather than being removed to achieve a denser layout.
The run header's Back button returns through application history, preserving
the originating listing/history URL and its filters, including after a reload.
A direct entry with no previous application location replaces the run with
Runs instead of sending the user outside the app or creating a return loop.
Run-view tabs use a single horizontally scrollable rail instead of wrapping
into multiple rows on narrow screens. The active view has a strong underline,
tinted background and semibold label; hover and keyboard focus stay distinct
in both themes. Radix tab semantics, arrow/Home/End navigation and the mounted
live graph are retained. Tab selection does not add history entries, so Back
still returns to the page that opened the run. All corners remain square.
Graph columns are vertically centered; branch/join connections use separate
ordered attachment points. Skipped-column dependencies travel above the cards
instead of crossing an intermediate node. High-contrast two-pixel strokes and
compact open-chevron arrowheads use matching two-pixel strokes and a straight
terminal shaft. Their centerline ends two pixels before the target border,
keeping the direction visible without clipping under the card.

Graph nodes use a single roving tab stop: Up/Down move within a column, Left/Right
choose the nearest row in the next column, and Home/End reach the first/last node.
Native Enter/Space selects a node; Tab leaves the graph. Focus scrolls the selected
viewport into view and survives live state changes. Every execution status has
readable text plus a decorative icon, never color alone; the shared status badge
also covers catalogue run statuses. The expandable ordered Node list exposes
statuses and dependency conditions without spatial navigation, with the same
selection and historical Task Plan links. Closed lists do not render a second live
node tree. Chromium, Firefox and WebKit run the actual signed-JWT production-build
keyboard fixture, including desktop-light/mobile-dark axe and overflow checks.

`WF_GRAPH_BROWSER=true` enables the separate real signed-JWT/PostgreSQL/MessagePack
Chrome performance fixture in `AuthorizationMatrixTests`. It builds the production
SPA into a private directory, provisions MSAL through a separate test-only entry
(never shipped in normal `dist`), and pans a 200-node/361-edge graph during 1,000
ordered durable transitions. No API responses, run state or browser transport are
mocked. Actual database receipts must sustain >= 100 events/s; page-specific Chrome
`DrawFrame` events and actual panning callbacks must each measure >= 60 fps without
rounding. Trace parsing rejects missing/ambiguous page owners, duplicate marks,
insufficient frames and trace data loss; final DOM states must equal the real API,
with unchanged geometry and no REST polling.

This is an uncapped rendering-capacity benchmark: Chromium runs with
`--disable-frame-rate-limit`, explicitly recorded in every report, so a 60 Hz
display cannot impose a sub-60 timing ceiling on the capacity gate. It is not a
claim about display refresh, tear-free delivery or every frame meeting 16.67 ms.
Local Windows Chrome 145 measured 169.781 actual drawn fps and 170.005 panning
callbacks/s at 123.641 committed events/s (p95 frame interval 9.399 ms, maximum
40.903 ms). Earlier default-vsync measurements were 59.315 and 51.839 fps and
did not pass.
Static edge layer separation reduced measured paint work from 1,761 ms to 209 ms
over comparable eight-second traces, but default-vsync smoothness remains a
separate measurement, not silently waived. Chrome traces and environment/throughput/
frame reports are retained under ignored `.agent-artifacts\run-graph-*`; CI uploads
them even on success. The private preview/cache and backend fixture clean up after
the run.

Commit links require an explicit workflow-repository browser template, for example:

```text
VITE_COMMIT_URL_TEMPLATE=https://github.com/example/workflows/commit/{commit}
```

Use exactly one `{commit}` placeholder. Only absolute HTTPS URLs without embedded
credentials are accepted; the full 40- or 64-character applied commit is substituted.
Links open a new tab without opener access. Missing configuration keeps the SHA
visible with a configuration notice; invalid configuration shows an error rather
than an unsafe link. The application's own repository is never assumed to own
registered workflows. Use a template only when it correctly identifies the
workflow repository for that deployment; per-workflow repository mappings are
not part of the current registration contract.

Schedule details link to `/series/<uuid>`; workflow list rows link to
`/t/<team>/<type>/w/<workflow>/history`. Both use the Viewer-scoped unified
history API and show durable runs alongside exact retained skipped/missed
outcomes and reasons, including facts with no run and history retained after
schedule/workflow deletion. Run status, duration, Manual/Scheduled/Catch-up
trigger and executed version are recorded facts, not current manifest guesses.
Version links open the historical Task Plan. Durations require both start and
end; unknown legacy details remain explicit. Variable columns show only set
schema-backed names/types, with Secret entries redacted; no values/defaults or
secret references are returned.

Committed abort details include original actor/name, exact time, reason and
source; a legacy Aborted status without its event does not invent attribution.
Typed URL filters restore status, exact aborted-by principal, outcome and
ascending/descending creation order after copy/reload. Principal identities
follow the backend's canonical lowercase nonempty GUID contract, without
imposing run UUID version/variant bits. Status/aborted-by filters exclude
fire-only facts. Local timestamps, explicit loading/empty/error states, refresh
and cursor-based older-page loading are accessible in light/dark and narrow
layouts. A failed older-page request preserves already loaded records; revoked
reads hide them.
The catch-up run has a Catch-up badge while its retained source outcome remains
Missed. Paging validates resource identity, filter matches and strict
timestamp/UUID/kind ordering, including microsecond ties. Trends remain separate.

The main navigation's Runs view (`/runs`) searches authorized durable run
summaries by exact team, type, workflow, series, named status, created-time range
and copied metadata. Type views provide a prefiltered entry. All filters combine
with AND; times require an explicit RFC3339 offset, with inclusive start and
exclusive end, preserving up to microsecond precision. Metadata filters are a
JSON object of exact strings, including empty values and significant whitespace.
Invalid or contradictory drafts show a form error without sending a request.

TanStack Router validates typed URL search parameters, including metadata grouping;
copying or reloading the URL restores the form and view. Grouping uses the run's
copied historical metadata, never its workflow's current metadata. Missing legacy
metadata remains explicitly unrecorded. Groups cover only loaded pages and may
grow after Load more runs; grouping alone does not repeat the server query.
Requests retain the server's descending keyset cursor and fixed 50-row page size.
Malformed, unordered, duplicate, out-of-scope or filter-mismatched responses fail
explicitly rather than being rendered as matching results.

The scrollable list renders at most ten fixed-height rows regardless of loaded
count, with logical list positions and full IDs available to assistive tools.
Page Up/Down works in the focusable scroll region. Use accessible full list
disables windowing for sequential keyboard/screen-reader access to every loaded
record; it does not fetch additional pages. Refresh and Load more runs are
explicit, without polling. Revoked access hides cached results. These summaries
do not expose variables, task payloads or storage capabilities; the richer
workflow/series history is a separate surface.

Live queued/running run pages expose Abort run only to a freshly authorized
Operator/Admin. The confirm dialog requires a nonblank reason of 1-500
characters, sends source `UI`, preserves the exact reason and remains open on
failure. Cancellation is never submitted by merely opening the dialog; pending
requests cannot be duplicated and failures never retry automatically. Viewer,
Author-only, terminal, reconnecting and already-aborting runs expose no action;
permissions and live status are checked again on submit.

The run's status badge follows authoritative committed state, not the HTTP
receipt. An accepted abort banner explicitly waits for committed cancellation;
recorded Aborting still permits cleanup, and only recorded Aborted says work
has stopped. Original actor/name, acceptance time, reason and source survive
reload and appear in series/workflow history. No actor is invented for legacy
Aborted rows without a record. Dialog cancellation restores the trigger; after
acceptance removes it, focus returns to the named Run controls region. The
controls are memoized on identity/status/abort/live state so node-event traffic
does not add dialog rendering work. Abort does not pause the schedule.

`run-abort.spec.ts` exercises actual confirmation, live MessagePack completion,
banner/history attribution and mobile/desktop accessibility on all three browser
engines. The signed-JWT permission fixture hydrates the existing MSAL test cache
instead of relying on development HTTP headers for WebSockets; actual Viewer
run pages show no Abort, issue no mutation and receive 403 on a direct abort.
No API/SignalR response is mocked; fixture authentication is not a live Entra
sign-in.

Run pages default to Graph and expose a keyboard-accessible Timeline tab.
Timeline loads only when selected and refreshes explicitly; graph events do not
poll or refetch it. Separate queue-wait and completed-execution lanes use only
recorded timestamps. Active attempts have no invented end or ticking duration.
Clock inversions remain visible as warnings rather than misleading bars.
The initially open native details table exposes every actual attempt in
manifest/task order, including its original attempt number, full task key,
recorded times in the browser's local timezone, queue/execution metrics,
outcome/category and historical agent ID. Missing legacy attribution remains
unknown; no current agent is substituted. The table works without interpreting
the spatial chart.

`run-timeline.spec.ts` runs through the optional backend timeline browser fixture
with actual dispatcher retries, agent-loss replacement, PostgreSQL/Kestrel and
the existing signed MSAL/MessagePack session. Chromium, Firefox and WebKit verify
the full table against the authoritative timeline API, distinct attempt agents,
keyboard tabs/table collapse, explicit refresh without polling and zero
critical/serious desktop-light/mobile-dark axe violations or page overflow.
No timeline API response or transport is mocked.

The **Metrics** tab reads attempt CPU average/maximum, peak RAM, network byte
totals and observed coverage through fresh Viewer-authorized resource endpoints.
Select an exact task/attempt, then task scope or machine context. Task CPU uses
one core = 100%; machine CPU uses the whole machine. Windows JobObject memory
includes descendants but task network transport bytes cover the host process
only. Linux cgroup memory/network includes descendants; network counts L3 bytes.
Machine non-loopback interface traffic is context, never task attribution.

The shared lazy uPlot viewer draws CPU, RAM and sent/received rates with null
gaps. At most 60 integrity-checked samples load per page; newer/older controls
follow signed attempt-scoped cursors without accumulating old chart arrays.
Sequence order survives UTC clock corrections: chart X is page-local sample
order, not inferred elapsed time. The keyboard-scrollable table preserves exact
64-bit byte counters, source timestamps, scope strings and recorded availability
reasons. MiB chart coordinates are approximate; absent values never become zero
and small nonzero measurements are not rounded to zero.

Metrics refresh explicitly, not by timer polling or invented live values.
Changing attempts aborts stale reads; errors hide the affected series, fresh
authorization denial hides all cached metrics, and refresh restarts at the
newest samples. `e2e/resource-charts.spec.ts` uses retained, integrity-checked
blob/PostgreSQL contract fixtures through real HTTP, not response mocks or a
claim that those fixtures ran on an OS sampler. Chromium, Firefox and WebKit
check native chart pixels, exact counters, bounded paging, both OS scopes and
unavailable/baseline reasons, machine context, owned-blob corruption/recovery,
light/dark mobile accessibility and no page overflow. Actual agent shipping and
sampler-overhead measurements have their separate backend evidence.

Development authentication is opt-in and requires both a loopback hostname and
the Vite development build. A production build rejects it even on localhost.
No token is fabricated by the UI; the backend's own `--dev` handler supplies the
local delegated development identity.

## Entra configuration

Without explicit development mode, authentication fails closed until these
public build settings are configured, for example in ignored `.env.local`:

```text
VITE_ENTRA_TENANT_ID=<tenant UUID>
VITE_ENTRA_SPA_CLIENT_ID=<SPA application UUID>
VITE_ENTRA_API_SCOPE=api://<API application UUID>/<delegated scope>
```

Set the backend's `AzureAd:SpaClientId` to the same SPA UUID and configure the SPA
redirect origin in Entra. MSAL handles delegated popup sign-in, silent token
renewal and sign-out, using session storage. Tokens stay in MSAL's session cache, never in React state,
URLs or logs; API requests use the Authorization header. Interactive
renewal failures surface with sign-in recovery instructions. Live Entra sign-in
requires a configured tenant and is not simulated by the browser acceptance suite.

`bun run build` emits route-split static assets in `dist`. Serving them from the
API at the same origin is a separate hosting task; the current backend does not
yet serve this directory. Do not deploy the development bypass or embed client
secrets in `VITE_*` settings.

## API contracts

`src\lib\api\schema.d.ts` comes from authenticated `/openapi/v1.json`;
`ui-schema.d.ts` comes from `/openapi/ui.json`. Schedule mutations use only the
internal client; detail reads use the public client. Runtime response validation
checks JSON shapes and ETag consistency before allowing further writes.

`src\lib\live\useRun.ts` exposes authoritative run state and explicit recovery
status for run-detail consumers. It subscribes before reading the graph, applies
live frame batches immutably, fills sequence gaps from the last applied cursor,
and reloads the snapshot on HTTP 409. Racing deltas stay bounded; overflow
requires a new snapshot rather than silently losing updates. Reconnects resume
without timer polling; failures clear stale state and expose retry through
`ReconnectingIndicator`. Unsubscription and authentication reset cancel old
responses. Unsafe JavaScript integer sequences are rejected before a rounded
cursor can be sent. Actual JWT browser fixtures verify dropped-frame recovery,
a 6,001-event gap, physical socket loss and complete API-host restart on all
three engines. The physical-fault fixture keeps committing durable events
while the API is absent, recreates its application/DI/listener at the same URL,
and verifies exact ordered recovery through the production client without
mocked API responses or REST polling. This is an API-host lifecycle test, not
an OS-process-kill or deployment-failover claim.

```powershell
$env:KARDIA_API_URL = "http://127.0.0.1:5001"
# Outside backend --dev, set KARDIA_API_TOKEN to an authorized bearer token.
bun run api:generate
bun run api:check
```

Generation treats open JSON objects as `Record<string, unknown>`, rather than
incorrectly forbidding variable properties. The check fails on any generated
contract drift. Do not manually edit generated files.

## Checks

```powershell
bun run lint
bun run build
bun run test
dotnet build ..\backend\src\Wf.ControlPlane --configuration Release
bunx playwright install chromium firefox webkit
bun run test:e2e
```

Browser tests require Docker Linux containers and the built Release host. The
fixture creates its own PostgreSQL 17 container, migrates it, starts the actual
`Program --dev` on HTTP/1 port 4174/HTTP/2 port 4175, registers a real workflow
through HTTP and starts Vite on 4173. All three ports must be free. No API mocks
substitute for CRUD, stale edits or previews. `KARDIA_HOST_DLL` can select an
isolated copy of the compiled host.

After changing API routes, `bun scripts\test-stack.ts --generate-api` can
bootstrap both generated clients against that isolated real host; stop the
helper with Ctrl+C when ready. Normal browser runs always check contract drift.
The history fixture seeds 60 retained skipped fires for paging; a separate
browser case waits for the real hosted scheduler to start a run and skip its
overlapping next fire without creating another run.
The read-only fixture registers an actual code schedule, verifies its applied
file/SHA/link and checks all four server-side 409 responses.

The fixture checks live public/internal OpenAPI drift before starting Vite.
Playwright's teardown explicitly signals the helper to stop both child processes,
remove its container and temporary storage, and delete its ignored state file;
this works without POSIX signal delivery on Windows. Docker failure, startup
failure and incomplete cleanup fail explicitly.

`e2e\schedule-crud.spec.ts` covers Chromium, Firefox and WebKit, light/dark/mobile
layouts, keyboard modal cancellation/focus restoration, actual 412 conflicts,
offline recovery and axe checks with zero critical/serious WCAG violations.
Unit tests cover draft limits, UTF-8 byte counts, DST labels, response validation
and fail-closed authentication/permission guards. The additional authorization
browser lane uses the actual JWT-authenticated Kestrel server and PostgreSQL role
bindings, not development API authentication or API mocks. It checks Operator →
Viewer → denied-scope action visibility and retained drafts in all three engines.
The same lane provisions both team-Viewer and type-only-Viewer catalogue users.
`e2e\catalog-isolation.spec.ts` uses signed fixture JWTs in MSAL's public cache,
then the unmodified production session/token client: no development bypass,
injected authorization headers or API response mocks. All three engines check
permitted navigation and latest-run data, absent foreign teams, guessed foreign
routes, real catalogue/run/event API denials, and desktop-light/mobile-dark axe
and overflow limits. Long team identifiers may wrap; tables retain their own
horizontal scrolling region instead of widening the page.

Its backend fixture seeds real PostgreSQL registry grants and complete durable
run snapshots, and starts a JWT-authenticated live API plus an isolated Vite
server on ephemeral ports. The wrapper supplies `WF_CATALOG_ORIGIN` only to its
child Playwright run; that run does not start or stop the normal schedule stack.
Default `bun run test:e2e` excludes this fixture-only spec rather than running
without its scoped identities. MSAL cache hydration is test credential
provisioning, not verification of a live Entra sign-in flow.

Run it from the repository root after installing the browser dependencies:

```powershell
$env:WF_PERMISSION_BROWSER = "true"
$env:WF_FRONTEND_ROOT = (Resolve-Path frontend).Path
dotnet test backend\tests\Wf.Integration.Tests --configuration Release --filter FullyQualifiedName~AuthorizationMatrixTests
```

Without the explicit browser opt-in, the .NET-only suite skips these browser
fixtures; the frontend CI lane always opts in. `.github\workflows\frontend.yml` runs
lint/build/units and the real PostgreSQL/browser lane on Linux; remote execution
is not implied by local checks.
