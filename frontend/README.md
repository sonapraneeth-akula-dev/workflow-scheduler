# Kardia frontend

React 19, TypeScript 5, Vite, Bun, TanStack Router/Query, Tailwind CSS 4 and owned
shadcn/Radix primitives. File routes are generated into `src\routeTree.gen.ts`;
commit generated route changes with their source routes.

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
defaults and schema validation come from the selected workflow version. The
schema-driven shared variable form is a separate task.

Large JSON numbers, including both Int64 limits, retain their exact source
tokens through preview, create/edit, response validation and reload. Native raw
JSON serialization avoids introducing a numeric wrapper dependency. Browsers
without the required native support fail explicitly before unsafe edits.

The server validates the complete draft and computes five upcoming UTC instants.
The UI shows them in the selected timezone with offset labels, including repeated
DST hours. Invalid fields, failed/offline previews and pending mutations disable
submission. Editing uses the response's strong ETag in `If-Match`; a real 412
preserves unsaved edits and requires an explicit reload. Pause/resume preserve
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

Home lists permitted teams; `/t/<team>` shows type descriptions and latest-run
status counts, and `/t/<team>/<type>` shows workflows with independent active
and latest versions. Home to workflow list takes two clicks. Never-run workflows
are explicit; pages load 50 workflows with manual continuation. Authorized
type-scoped MessagePack updates refresh the affected workflow list and team
counts without timer polling or requiring a broader team grant. Failed live
connections expose retry and manual refresh; denied reads hide previously cached
rows. Team presentation, search/grouping and run detail links remain separate
tasks.

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

Schedule details link to `/series/<uuid>`. The read-only, Viewer-scoped fire
table shows exact recorded outcomes, reasons and associated run IDs, including
skips/misses with no run and history retained after schedule deletion. UTC
timestamps, explicit loading/empty/error states, refresh and cursor-based
older-page loading are accessible in light/dark and narrow layouts. A failed
older-page request preserves already loaded records. A linked catch-up run
has a Catch-up trigger badge; its original scheduled-fire outcome remains
Missed. The badge uses the durable source marker and run reference, not a
reclassification of missed fire history. Full run history and
trends remain separate tasks.

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
Run it from the repository root after installing the browser dependencies:

```powershell
$env:WF_PERMISSION_BROWSER = "true"
$env:WF_FRONTEND_ROOT = (Resolve-Path frontend).Path
dotnet test backend\tests\Wf.Integration.Tests --configuration Release --filter FullyQualifiedName~AuthorizationMatrixTests
```

Without the explicit browser opt-in, the .NET-only suite skips that one browser
case; the frontend CI lane always opts in. `.github\workflows\frontend.yml` runs
lint/build/units and the real PostgreSQL/browser lane on Linux; remote execution
is not implied by local checks.
