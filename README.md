# Kardia Workflow Scheduler

Implementation follows `docs/plans/2026-10-15_workflow-scheduler_azure-task-list.md`.

## Frontend

`frontend` contains the React/TypeScript schedule-management SPA: create, edit,
pause, resume, delete, timezone selection and server-calculated next-five-fire
preview. Bun manages dependencies and scripts; Vite proxies API traffic to the
control plane. See `frontend\README.md` for local/Entra configuration, generated
public/internal clients, actual PostgreSQL/browser tests and current hosting
boundaries. Code schedules show applied file/commit provenance without mutation
controls; commit-browser links use an explicitly configured HTTPS template,
never an assumed application repository.

Schedule details link to `/series/<uuid>` for durable scheduled-fire history.
`GET /api/v1/series/{id}/fires` requires fresh Viewer access to the owning
team/type, independently of the SPA client. Responses contain `seriesId`,
`items` (`fireAt`, `outcome`, nullable `runId`/`reason`) and nullable
`nextCursor`. Fires are newest first, including `Skipped`/`overlap` without a
run and retained history after schedule removal. Pass the opaque cursor back
as `?cursor=...&limit=50`; limits are 1-100. Invalid pagination returns 400,
unknown or unauthorized series 403, and responses are never cached. This
view lists fire history, not the separate full run-history/trends feature.

Catalogue and scheduled-fire run links open `/runs/<uuid>`. Its Task Plan link
uses the run's immutable version UUID, never the currently active version.
`GET /api/v1/workflows/{team}/{type}/{workflow}/versions/{version}` requires fresh
Viewer access to the exact type and returns an explicit safe plan projection:
ordered nodes/tasks, dependency conditions, lifecycle stages, effective placement,
retry policies and timeouts. Historical versions remain readable after workflow
soft deletion; missing versions return 404, denied scopes 403, invalid identities
400, and responses are never cached.

SDK manifests now record optional declared task actions: the original SDK method,
command executable, credential/query/fragment-free one-shot HTTP method/URL, or
declared HTTP operation. Description never executes tasks or inspects captured
values. Task Plan excludes command arguments/environment, HTTP headers/bodies,
variable values/defaults, input bindings and package/source capabilities.
Only declared safe secret references are shown. Older manifests without action
metadata explicitly say the declaration is unavailable; functions do not predict
commands they may generate at runtime.

## Backend

Requires the .NET 10 SDK. PostgreSQL integration tests require Docker with Linux
containers, or `WF_TEST_POSTGRES` pointing at a PostgreSQL 17+ test server.
External-server tests create and remove only their own unique schemas; the test
user needs schema-creation permission. CI uses Testcontainers on Linux and a
service PostgreSQL on Windows.

```powershell
dotnet build backend\Wf.slnx
dotnet test backend\Wf.slnx --collect:"XPlat Code Coverage"
dotnet run --file backend\samples\workflows\scenario-a.cs -- --var env=dev
```

The sample executes scenario A's four-node DAG, a real command, illustrative
deployment/check functions, and lifecycle stages through the shared engine core.
Node/task states print live. Exit code is 0 only for `Succeeded`; failures,
timeouts and aborts return 1. Ctrl+C requests an abort and allows cleanup.
`LocalVariables.Parse` accepts repeated `--var key=value` pairs; strings, enums,
durations and secret-reference names use plain text, other types use JSON.
All policies and variables validate before tasks start.

`Wf.Engine.Core.FailureClassifier` reuses the existing `FailureCategory` contract:
TaskError, Timeout, Aborted, AgentLost, ResourceLimit, ValidationError, Placement
and PlatformError. Local execution and the SDK task host preserve explicit
`TaskFailureException.Category`/nonretryable metadata before considering
`TimeoutException` or actual timeout/abort facts. Bare/self cancellation without
an engine cancellation cause is an author TaskError, not an assumed Aborted;
ordinary author errors are not relabeled by concurrent cancellation. Unknown
origins default to PlatformError; callers inside task execution supply TaskError.
No exception-message or generic out-of-memory/argument-error guessing occurs.
Attempt lifecycle status and existing author/platform retry budgets remain
separate from category. Scripted typed signals exercise all eight categories
through actual local and child SDK hosts; this does not implement every external
fault producer. The committed metrics path described below preserves these known
categories through gateway completion, durable attempts/events and replay.

`RunSnapshot.Metrics` projects run, lifecycle-stage/node, task and attempt timing
only when committed events are applied. Queue wait is separate from execution;
task duration sums actual running-attempt intervals, excluding retry backoff,
placement wait and recovery-handler delay. Retry counts increase on another
actual dispatch, not a scheduled retry that is later cancelled. Every persisted
dispatcher attempt has `queue_wait_ms`. Prestart loss has zero execution;
aborted, reused and skipped entities retain their actual lifecycles. Durable
attempt identities and existing author/platform retry budgets are preserved.

The shared projection uses creation/event timestamps rather than another clock.
Full replay and serialized snapshot-plus-tail recovery agree; legacy snapshots
without metrics reconstruct from their original sequence-zero snapshot and
retained committed history. Missing history fails explicitly rather than
inventing times. Failed transactions leave caller-visible proposals unchanged.
These are transition metrics, not process-resource sampling.

`GET /api/v1/runs/{id}/metrics` requires fresh stored-resource Viewer access and
returns committed run, lifecycle-stage, node, task and attempt timing, actual
retry totals and known failure categories. Active values are recorded values,
not a second ticking clock. Pending accepted runs expose their queued baseline;
skipped/reused/nonstarted work does not acquire invented attempts or timings.
The response includes the authoritative sequence and no variables, metadata,
error messages, outputs, payloads or storage capabilities.

Typed producer categories survive gateway completion, `task_attempts`, committed
events, snapshots and replay without changing retry budgets or the existing enum.
Unknown/absent/numeric/noncanonical wire names remain unknown; messages are never
classified by guessing. A parent without an explicit cause exposes a category
only when its terminal failing children have one unambiguous known cause.
Historical attempt failures remain after a later retry succeeds. Responses are
never cached; malformed identities/queries return 400, denied scopes 403,
inconsistent persisted data 500 and unavailable storage 503. Legacy metrics-free
snapshots rebuild from retained original history instead of fabricated zeros.

Secret variables accept `secret://<store>/<name>` through the existing
`SecretReference` contract and `.Secret("token", "secret://local/deploy-token")`.
`SecretReference.Parse` requires a qualified reference; `Store` and `SecretName`
are computed, not serialized. Existing safe bare/hierarchical names remain
local-store references for compatibility. Store aliases use 1-63 lowercase
letters/digits/hyphens; secret names use 1-200 ASCII letters/digits, `.`, `_`, `-`
and safe slash-separated segments. Credentials, query strings, fragments,
percent escapes, whitespace and traversal segments are rejected without echoing
the input. Manifests still store exactly `{"Name":"reference"}`; literal string
secret defaults and extra value fields fail shared WFV008 validation. A
name-looking string inside `Name` cannot be distinguished from an intended
reference; never put resolved values there. Resolution and value masking follow
in their own agent tasks.

`Tasks.HttpOperation(...)` declares an opt-in long-running HTTP operation:
submission URL, status/state mapping and retention, optional cancel URL,
deduplication header/retention, lookup-by-key contract, trusted origins and secret
reference. Attach the returned `HttpOperationTask` directly with `.Task(...)` so
its metadata reaches the manifest; passing its delegate alone loses that contract.
Shared WFV021-WFV023 rules reject unsafe URLs/templates and require both
`.NoAutoRetry()` and zero author retries without deduplication or lookup. Retry
contracts must retain remote identity through the entire run timeout. Ordinary
`Tasks.Http` remains one-shot, including 202 responses.

Only authoring/manifest/validation is available yet: attempting to execute an
HTTP operation explicitly fails with nonretryable
`HttpOperationRuntimeUnsupportedException`, without sending a request. Durable
checkpointed submission, polling, reconciliation, remote cancellation and System
Events follow in their prerequisite tasks. A lookup miss alone must never
authorize resubmission without a service deduplication guarantee.

`await workflow.RunAsync(args)` selects local execution by default, `--describe`,
`--engine <origin>`, `--task-host --socket <absolute-path>`, or
`--dry-run <mocks.json>`. Modes are exclusive; repeated `--var key=value` pairs
work before or after local/remote/dry-run mode flags. Describe emits only the
manifest, without binding required run values or executing tasks.
Remote mode validates variables and POSTs `workflowId`, exact `version` and
overrides to `/api/v1/runs` with an idempotency key, then prints the accepted
run UUID. It does not upload/register the workflow or wait for completion.
Non-loopback HTTP, redirects, invalid/oversized replies and rejection statuses
fail explicitly.

Stage and node task delegates publish with
`await ctx.Reports.PublishAsync(kind, title, payload, ctx.CancellationToken, tags)`.
Inject `new ReportPublisher((publication, token) => ...)` through the existing
`LocalEngine(..., reports: publisher)` port. Every publication carries the
registry's exact full source key, including `$post/summary` for a named post-stage
task; parallel delegates never share mutable source state. Payload bytes and
tags are copied before the sink awaits. Kind/title/tags must be nonblank, unknown
kinds remain valid, and metadata is not trimmed or rewritten. Empty payloads are
valid. Cancellation and sink failures propagate into the ordinary task failure
policy; the publisher does not retry. Missing or unbound publishers fail
explicitly. Existing custom `IReports` implementations remain compatible.
The default SDK quota is 10 reports per run, each at most 10,000,000 bytes
(decimal 10 MB). Successful and in-flight publications share an atomic budget
across all contexts; oversized payloads fail before copying or calling storage.
Sink failure/cancellation releases its local reservation without hiding the
original exception. Reusing the publisher for another LocalEngine resets that
run's budget. `ReportLimitExceededException` is nonretryable and follows the
ordinary Hard/Soft task failure policy; post-stage cleanup still runs.
Pass a validated `Wf.Contracts.ReportLimits` as the publisher's second argument
for overrides. Its `FromTypeLimits` parser accepts the `workflow_types.limits`
shape `{"reports":{"maxCount":10,"maxBytes":10000000}}`, with either field
optional. Values must be positive integers; unknown or duplicate report settings
fail explicitly. Other limit sections remain independent. Shared contracts keep
SDK/server policy parsing identical; the local counter is not a durable storage
receipt.

Remote task hosts now receive an attempt-scoped report capability from the real
agent gateway. Its optional protobuf `bearer_token` field leaves old grants
wire-compatible and keeps credentials out of request URLs. The agent accepts
report destinations only on configured engine origins, with no credentials,
query, fragment, alternate endpoint or duplicate grant. The SDK uses exact
HTTP/2, no redirects and the Authorization header, then validates a bounded
committed receipt. Publishing requires a real Dispatched/Running attempt in a
Running/Aborting run; no user-supplied source or cross-run override is accepted.
Capability expiry is the attempt deadline plus 24 hours for immutable retries.

`POST /api/v1/report-publishes/{reportId}` streams only payload bytes to the
existing blob store. Bounded metadata comes through `X-Wf-Report-Metadata` as
base64url JSON: kind, title, tags, exact length and SHA-256. Kind/title/tag limits
are 128/512/128 UTF-8 bytes, with at most 32 tags; local SDK publishing applies
the same description rules. The server reads the actual workflow type's limits
and serializes admission in PostgreSQL across all hosts, without holding engine
run-state row locks during upload. It verifies length/hash and immutable
`reports/{runId}/{reportId}` content before committing a metadata/pointer-only
report row. A compact global ID locator prevents cross-run/month ID reuse while
the report records remain monthly partitioned.

The same report ID with exact source/attempt/metadata/content replays without
consuming quota or emitting another event, including after task completion.
Different content conflicts. A committed row emits transactional `ReportAdded`
notification IDs only; rollback emits none. Blob success followed by database
failure can leave an unreferenced immutable object; an exact retry can repair the
pointer, and retention cleanup must collect unreferenced objects/ID locators.
Failed or cancelled streams leave no report row or final object. Signed-token,
permission, malformed-policy, count, byte, storage and receipt failures remain
explicit. This upload endpoint is not a viewer download capability.

`GET /api/v1/reports/{id}/content` requires fresh Viewer permission on the
report's durable run scope (including historical runs whose workflow was
deleted). It streams verified immutable bytes without buffering the whole report
or exposing storage paths/grants. A late size/hash fault aborts the response,
never successfully completes a partial body. All successful content responses,
including direct navigation, carry CSP `sandbox allow-scripts; frame-ancestors
'self'; default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline';
img-src data: blob:; font-src data:; connect-src 'none'; form-action 'none';
base-uri 'none'`, `nosniff`, and `no-store`; only these
responses omit the default `X-Frame-Options: DENY`. Denials/errors retain DENY
and a stricter sandbox. Known exact kinds use safe MIME types; untrusted titles
never become filenames. Generic `image` and unknown kinds download as
`application/octet-stream` (clients may identify raster magic on fetched bytes);
explicit `png`, `jpeg`, `svg`, and their exact MIME kinds have truthful types.
The content OpenAPI response is a binary string, not a reflected Stream object.
Inline scripts/styles and embedded visuals work without allowing fetch, forms,
base-URL overrides or external resources. Source restrictions do not prevent
every self-frame navigation. Authenticated clients fetching a Blob must preserve
equivalent CSP/sandbox restrictions when rendering it; response CSP is not copied
automatically to an object URL.

`GET /api/v1/runs/{id}/reports` requires fresh Run Viewer permission and returns
`{runId,items:[{id,runId,sourceKey,attempt,kind,title,tags,sizeBytes,createdAt}]}`.
All fields are required/non-null; source/attempt are exact historical facts and
timestamps are committed UTC, not active clocks. No payload, hash, blob path,
capability or variables appear. Optional single `kind`/`tag` filters are exact,
case-sensitive, nonblank, at most 128 UTF-8 bytes, and combine with AND.
Duplicate/unknown filters fail with 400. Results use stable `createdAt,id` order
and `no-store`.

`GET /api/v1/series/{id}/reports/compare?leftRunId=...&rightRunId=...&sourceKey=...&title=...`
looks up the same exact source/title in two distinct actual runs of the stored
series. Returns `{seriesId,left,right}`, with each side the same required,
metadata-only report item above; fetch content separately. Fresh Viewer grants
must cover the series and **both** runs, including retained historical ownership
after workflow deletion. Missing/foreign/ungranted resources uniformly return
403 `insufficient_scope_role`, without exposing either side's metadata.
Series IDs use canonical lowercase nonempty UUIDs; run IDs require canonical
RFC UUIDv7. All four query values are required exactly once; unknown/duplicate
keys or invalid identities return 400 `invalid_report_compare_query`.
Source/title matching is case-sensitive, preserves author whitespace, Unicode
and historical stage/task keys without normalization, and is bounded to
1025/512 UTF-8 bytes respectively. Neither value may be blank or contain NUL.
If either match is absent, 404 `report_match_not_found` identifies neither side.
If both exist but either run has multiple matches (even across attempts or
kinds), 409 `report_match_ambiguous` requires an unambiguous source/title:
no arbitrary, newest or cross-attempt selection. Missing takes precedence over
ambiguity. Dependency failures return 503 `report_compare_unavailable`.
Comparison is read-only, bounded to two matching metadata rows per run partition,
and uses `no-store` with no content, pointer, capability or report metadata headers.

The SQL `report_added` notification is consumed by the existing live listener.
A separate immediate `ReportAdded` MessagePack message `{runId,reportId}` reaches
only an authorized subscribed run scope, with fresh role checks on every send.
It is an index invalidation, **not** a lifecycle event or `RunDelta` sequence.
The durable fact is the report row: on live `Resync` for a run, listener reconnect,
or client transport reconnect/resubscribe, explicitly refetch the report index
alongside normal durable run resynchronization. Notifications missed while
disconnected cannot be replayed; the index recovers them without REST polling.
Pending overflow or malformed/unavailable notifications also request explicit
resynchronization instead of silently losing updates.

`POST /api/v1/runs` accepts `workflowId`, optional exact `version` (SemVer or
version UUID), and optional `variables` (an empty object by default). A delegated
Operator or scoped Admin must own the workflow's team/type; Author and Viewer
alone cannot trigger. Global Admin also works for development/service calls.
The authenticated `oid`, never a request field, supplies the triggering actor.
Omitted versions resolve through the hosted active-version cache; explicit
versions must belong to the same undeleted workflow. Selected UUIDs remain
pinned through later activations. An unavailable cache returns 503 rather than
using stale data.

All nine variable types and workflow defaults resolve through shared
`Wf.Validation`; invalid/unknown/missing-required values return 400
`application/problem+json` with `errors[]` paths and hints, without echoing values.
Malformed, duplicate, null-required or unknown request fields fail closed;
placement overrides remain unsupported until their own task. Bodies above
1 MiB return 413 and non-JSON content returns 415. Acceptance atomically inserts
the immutable initial snapshot/deadline command and emits PostgreSQL `commands`
NOTIFY, then returns `202 {"runId":"<UUIDv7>"}`. Only leader recovery creates
the durable run rows. The run-detail endpoint remains a separate task.

Optional `Idempotency-Key` accepts one 1-200-character visible ASCII key,
without whitespace. PostgreSQL deduplicates for 24 hours per authenticated
principal and route, across concurrent API hosts and restarts. A matching retry
returns the original 202/run ID, even after activation or while the active cache
is unavailable; scoped authorization is checked again on every retry.
Whitespace, JSON object order and omitted request defaults do not change the
fingerprint; array order and supplied values do. Reusing an unexpired key for
a different request returns 409 without echoing either body. Missing keys
leave submissions independent; at expiry the key can identify a new request.
Reservation, command, response and NOTIFY commit together, so validation or
persistence failures cannot leave an orphan run command or consume the key.
Expired rows stop deduplicating immediately; general physical retention
cleanup remains a maintenance task.

`POST /api/v1/schedules/{id}/run-now` accepts no body or `{}`. Scoped Operators
and Admins can manually launch code- or UI-origin instances, including paused
ones, without resuming them or changing their next-fire cursor. The command
captures the instance's effective variables and existing permanent series;
leader recovery persists it with the Manual trigger. Removed instances return
404 for new requests. Keyed retries return the original run even after edits
or removal, subject to fresh scope authorization; reusing that route's key for
a different instance returns 409.

An omitted schedule pin selects the active version. Pins accept an exact UUID
or SemVer, comparisons (`=`, `>`, `>=`, `<`, `<=`) joined by spaces (AND),
`||` alternatives, and `*`, `major.*` or `major.minor.*` wildcards. Ranges select
the highest matching published version from the same undeleted workflow,
excluding prereleases unless a comparison explicitly names a prerelease with
the same major/minor/patch. Numeric components have no Int32/Int64 ceiling;
build metadata does not affect precedence. Pins are limited to 256 characters;
caret/tilde/npm shorthand is not supported. Shared WFV026 validation reports
invalid pins before code schedules are registered. Invalid effective values
or unavailable pins fail without creating a command; run-now does not create
a scheduled-fire row or apply recurring overlap decisions.

User-defined schedule management is UI-only: `POST /api/v1/schedules`,
`PUT /api/v1/schedules/{id}`, `POST .../{id}/pause`, `POST .../{id}/resume`,
`DELETE .../{id}`, and `POST /api/v1/schedules/preview`. Production must configure
`AzureAd:SpaClientId` (`AzureAd__SpaClientId`); absence disables these routes with
403 while leaving other API authentication unchanged. Only authenticated
delegated user tokens with `scp`, a nonempty actor UUID and the exact SPA `azp`
pass this gate; app-only tokens, other clients and `appid` fallback do not,
including global Admins. Team/type Operator or Admin authorization still applies.
Explicit `--dev` authentication supplies a local delegated identity only on these
UI routes, preserving existing local Registrar/Agent behavior elsewhere.

The request contains `workflowId`, `name`, `timezone`, exactly one `cron` or
whole-second `interval`, optional `versionPin`, `overlap`, `catchUp`, and variable
overrides. Validation uses the selected version's schema and shared schedule
rules. Effective defaults are persisted. Preview returns the next five UTC
instants with the same IANA/DST calculator without writing anything. Bodies are
strict JSON capped at 1 MiB; names allow at most 128 Unicode code points.
`GET /api/v1/schedules/{id}` remains a public authenticated Viewer read and
returns the current strong ETag. Reads and `run-now` have no SPA-client restriction.

Schedule reads include `origin`, nullable `sourcePath` and nullable `gitCommit`.
Code provenance comes from that instance's exact applied schedule configuration,
not the active workflow version or the latest registration attempt. A
schedule-only file edit can update its path/commit without changing the workflow
version or series; an identical-content retry preserves the original provenance.
User-origin and legacy code rows without retained provenance return explicit nulls.
No file content or resolved secrets are included. All code edit/delete/pause/resume
routes reject authorized callers with 409 before requiring a UI edit ETag.

Every edit/pause/resume/delete requires exactly one strong numeric `If-Match`
ETag: missing is 428, malformed/weak/wildcard/multiple is 400, stale is 412.
Create returns 201 with `Location` and ETag; edit returns 200, pause/resume 202,
delete 204. Code-origin mutations return 409. A schedule cannot move workflows;
its permanent series survives deletion. Unchanged expressions keep their
intended fire cursor, expression edits recompute it, pause clears it, and resume
starts at the next occurrence after now instead of catching up an intentional
pause. Semantic no-ops retain ETag/cursor/audit state. Mutations, sanitized audit
attribution and scheduler notifications commit atomically.

Authenticated OpenAPI 3.1 documents use `Microsoft.AspNetCore.OpenApi`:
`/openapi/v1.json` includes public reads and `run-now`, never UI-only mutations;
`/openapi/ui.json` contains only the SPA schedule-management routes. Request
types for those routes live in the control-plane assembly, not the SDK or CLI.
Public and internal TypeScript clients are generated from those live documents;
the frontend's strict drift check rejects stale generated contracts.

`dotnet run --project backend\tests\Wf.Benchmarks -c Release -- --trigger-api-bench`
starts an isolated real API/PostgreSQL benchmark: 50 warmups, then 500 fresh-key
submissions at open-loop 50 requests/second. JSON output includes HTTP latency,
dispatch lag, offered/achieved rates and durable row counts; exit is nonzero
unless every request succeeds and p95 is strictly below 100 ms. Database and
host setup stay outside measurements. `WF_TEST_POSTGRES` uses a unique disposable
schema; otherwise the benchmark creates and removes its own PostgreSQL container.

The all-mode entrypoint requires ordinary, untrimmed IL because it includes the
gRPC task-host server. File-based workflows use `#:property PublishAot=false`,
as the sample does. The standalone SDK definition, manifest and local APIs
retain their AOT-compatible implementations; the agent still publishes native.

Unit and integration tests cover authoring, real task execution and PostgreSQL
event replay. CI runs on Windows and Linux. Frontend tests will accompany the
frontend implementation.

Provision the PostgreSQL database/schema and configure
`ConnectionStrings__Workflow`, then run the explicit deployment-only migration
mode before starting the control plane:

```powershell
dotnet run --project backend\src\Wf.ControlPlane -- --migrate
```

This exits after applying embedded, ordinally ordered scripts; it starts no
server or leader. DbUp PostgreSQL 7.0.1 journals each successful script in
`schema_versions` in the connection's current schema. A schema-scoped advisory
lock serializes deployers; each script and journal entry share one transaction,
with a 60-second command/lock bound. Existing outer BEGIN/COMMIT wrappers are
removed for this runner; variable substitution is disabled for PostgreSQL dollar
quotes. Failed scripts roll back and fail the process; rerunning skips committed
scripts. Existing manually provisioned, unjournaled schemas require a reviewed
baseline/adoption procedure, not an automatic assumption that all scripts ran.

`MigrationLint` rejects DROP/RENAME/TRUNCATE and added NOT NULL constraints
without a non-null default declared in that script, ignoring comments and
quoted values while checking explicit function/DO-body DDL. It is a static
compatibility check, not a SQL sandbox or proof about dynamic SQL/default
expressions; review those and release contract changes separately, at least one
release later. Initial migration `0003` now supplies the default needed by old
writers; `0013` supplies it to existing schemas without changing existing
deadlines. Normal run creation still writes its resolved policy deadline.

The control plane authenticates with Microsoft.Identity.Web 4.16.1 by default.
Set `AzureAd__TenantId` to the tenant UUID and `AzureAd__ClientId` to the API
application UUID; optional `AzureAd__Instance` defaults to
`https://login.microsoftonline.com/`. Configure the API audience in the Entra
application (and `AzureAd__Audience` when using a custom application ID URI).
Register app roles `Admin` (users/groups or applications), `Registrar` and
`Agent` (applications only), assign them to the appropriate principals and add
the optional `idtyp` access-token claim to the API app registration. Machine
policies require `idtyp=app`, the exact app role, and no delegated `scp`.
Signed JWT issuer, audience, tenant and lifetime are validated; untrusted tokens
receive 401 and authenticated callers missing the required policy receive 403.
Fallback authorization protects new endpoints; only explicit health endpoints
and capability-protected raw upload routes are anonymous. The agent gRPC
gateway requires the `Agent` policy.

For isolated local development, set `DOTNET_ENVIRONMENT=Development` and pass
`--dev` (or `Authentication__Mode=Development`). This deliberately bypasses
tokens with one fixed local identity holding all three global roles; never
expose this mode to untrusted callers. It throws outside Development, including
Production, Staging and Ppe. Development alone does not enable the bypass.
No identity or role is taken from caller-supplied development headers. Migration
mode does not need identity configuration. Live corporate sign-in, browser
MSAL and agent token acquisition remain separate tasks; offline integration
tests use real RSA-signed tokens and the normal validators with local metadata,
not a live Entra tenant or mocked authentication handler.

Task delegates resolve by stable `node/task` keys through `TaskRegistry`; no
assembly scanning. Contexts expose `Var<T>`, AOT-safe `VarEnum<T>`, declared
`Input<T>`, bounded `Outputs`, cancellation, logging and injected report/artifact
publishers. Publishing without a configured host publisher fails explicitly.
Native AOT/trim analyzers run for the SDK and shared contracts.

Core abort plans preserve the first accepted who/when/reason/source record.
Hosts commit the initial abort event before cancelling attempts, drain active
work, then run eligible `always` and post stages with `ctx.Abort`. Queued aborts
skip all work; running aborts record `EndedAt` only after cleanup actually stops.
Abort targets and the original record survive snapshot/event replay.

`POST /api/v1/runs/{id}/abort` requires fresh Operator or Admin access to the
stored run's team/type, independently of the SPA client. Send
`{"reason":"Stop deployment","source":"API"}`; source defaults to API and accepts
UI/API/CLI only. The nonblank reason is required, at most 500 characters; unknown,
duplicate and malformed JSON properties fail explicitly, and bodies are capped
at 16 KiB including streamed requests. The authenticated principal UUID/display
name and server UTC acceptance time cannot be supplied by the caller.
Acceptance atomically persists one Abort command and `run.abort` audit record,
then notifies recovery only after commit; the API never writes run state.
Concurrent/repeated requests, including while Aborting, return 202 with the first
immutable `{by,byName,at,reason,source}` record without another audit row.
Terminal runs return 409 Problem Details with their current `runStatus`; unauthorized/unknown runs
are indistinguishable 403. Pending accepted triggers can be aborted before run
materialization. Row locks serialize acceptance against terminal commits, while
repeat reads avoid recovery's command-row lock; failed audit writes roll back
acceptance and notification. Responses from authorized endpoint handling are
never cached. The CLI command is documented below; browser confirmation remains
its own task.

Attempt/node/run deadlines and retry backoff use a `TimeProvider`; cleanup gets
fresh cancellation tokens and its own deadlines. A function that ignores
cancellation beyond grace causes an explicit error, not a false terminal result:
the in-process local engine cannot forcibly kill managed code. Distributed task
hosts provide process isolation; built-in command cancellation also kills and
reaps its own process tree.

`RerunPlanner.Plan(newRunId, source, pinnedNodes, fromNodes)` creates a new
Queued/sequence-zero core snapshot with the same version UUID, metadata and
cloned variables, plus parent lineage. Unsuccessful nodes and their downstream
closure reset to Pending; unaffected Succeeded/Reused body nodes and tasks
become Reused with cloned original JSON outputs and `ReusedFromRunId`. Reusable
nodes require an explicit persisted output object, including `{}` for no outputs;
missing data fails instead of silently losing it. Selected successful nodes
also invalidate downstream outputs for every dependency condition.

New runs execute lifecycle stages again; invalidating a setup stage invalidates
its implicit body dependents. A successful source with no selection means a
full rerun; explicit selection must be nonempty, unique and declared. Source
runs must be terminal and match the pinned acyclic node/task definition.
Old abort, completion timestamps and attempt states do not carry over. Tests
plan from actual failed SDK execution and replay the planned shared core;
the HTTP rerun command, persistence of lineage/outputs and UI remain separate
tasks, not an available hosted rerun endpoint.

Dry-run executes the same core using explicit task mocks, without invoking any
original task delegate or external service:

```powershell
dotnet run --file backend\samples\workflows\scenario-a.cs -- --dry-run backend\samples\workflows\scenario-a.mocks.json --var env=ppe
```

Dry-run stdout contains one source-generated JSON event log, including snapshot, outputs,
attempts and final exit code. Mocks support `Succeed` with outputs, `Fail`,
`Timeout`, `Throw`, and `FailThenSucceed` with `failuresBeforeSuccess`. Each task
needs its own mock unless the spec explicitly supplies `default`; missing mocks,
unknown keys and unknown JSON properties are errors, not automatic success.
Spec `variables` override workflow defaults; command-line `--var` values win.
Timeouts/backoff advance virtual time with deterministic midpoint jitter and no
real delays. The testing library publishes file apps once through `ScriptBuildCache`:

```csharp
var workflow = await WorkflowUnderTest.BuildAsync("workflow.cs",
    additionalInputs: localDependencyDirectories, cancellationToken: cancellationToken);
var mocks = new MockSpec().FailNThenSucceed("build/compile", 2);
var log = await workflow.RunAsync(mocks, cancellationToken);
```

`WorkflowUnderTest` launches the actual published assembly out of process for
each run, avoiding file-app entry-point limitations. `MockSpec` is immutable,
requires explicit task mocks or an explicit default, and supports outputs,
variables, succeed/fail/timeout/throw and fail-N-then-succeed. Workflow retry
budgets still apply; timeouts/backoff use virtual time. Valid failing workflow
logs return normally; invalid host responses and exit/log mismatches throw
`WorkflowRunException` with stdout/stderr. Cancellation kills/reaps the owned
process tree and removes invocation files. Workflows are trusted code, not a
sandbox; use cancellation to bound real host lifetime.
`Wf.Sdk.Testing.Assertions` supplies framework-independent `DryRunLog` extensions
for run/node/task status, skipped work, completion-before-start order, persisted
retry attempts, lifecycle stages and structural/type-sensitive JSON outputs.
Assertions return the original log for chaining; mismatches or unknown keys
throw `RunAssertionException`, rather than silently passing.

Code-defined schedule files use the published editor schema at
`backend/src/Wf.Contracts/Schemas/schedules.schema.json`; see
`backend/samples/workflows/scenario-a.schedules.json`. `ScheduleFileSchema.Export()`
generates it from the same strict, source-generated contracts used by the server.
`defaults` groups variable values by category; each instance supplies a stable
`id`, exactly one `cron` or positive whole-second constant-format `interval`
(at most 2,147,483,647 seconds), and an IANA `timezone`.
Instance `variables` override defaults; optional `versionPin`, `overlap`
(`Skip`, `Queue`, `Concurrent`) and `catchUp` (`None`, `Latest`) control execution.
Default policies are `Skip` and `Latest`.
`ScheduleFileValidator.Validate(utf8, variableSchemas)` resolves workflow defaults,
then categorised file defaults, then each instance's overrides. It returns
immutable effective JSON values and shared `ScheduleExpression` objects, or
file/path/message/hint errors without partial instances. Unknown or invalid
values, required-but-missing variables and duplicate defaults across categories
fail even when another override would hide them. `Validator` and the testing
facade automatically check optional `schedules.json`; registration uses the same
validation and retains the exact UTF-8 file bytes.

Migration `0007_schedules.sql` adds durable code/UI schedule rows, stable code
keys, expression/policy checks, optimistic `row_version` and a partial due-fire
index excluding paused/deleted rows. Storage follows the design's positive
whole-second `interval_s`; subsecond interval precision is not represented.
Migration `0008_schedule_config_versions.sql` adds independent content-addressed
schedule-file provenance (source path, Git SHA, SHA-256 and attribution).
Composite foreign keys prohibit cross-workflow links; UI schedules cannot link
code configs, and old code rows remain valid during upgrade.
Migration `0018_schedule_file_application.sql` retains file content with a
database-checked SHA-256, adds the workflow's same-workflow active-config pointer,
and creates/backfills one permanent `run_series` per schedule. Atomic code-file
application is part of registration; UI mutations and leader execution remain
their own tasks.

`ScheduleCompatibilityRule.Validate(scheduleUtf8, targetApi)` applies those same
variable checks to a target `ApiBaseline`. Removed/renamed nodes do not affect
schedule compatibility; removed variables still referenced by defaults or
overrides do, even after a valid MAJOR bump. Version-pin resolution chooses the
target API before this check; the rule does not silently select another version.

`TeamRegistryRules` validates strict `team.json` contracts: owning team, type
name/description, metadata keys with required/allowed values, and optional
free-form metadata. Duplicate/normalization/edit-distance conflicts and the
trusted per-team type quota (default 10) fail with WFV015-WFV020 diagnostics.
`Validator` checks the nearest optional `team.json` in the workflow directory or
its first two parents, matching `teams/<team>/<type>/<workflow>`. It checks the
manifest's type and metadata against that registry; invalid registry and schedule
errors accumulate rather than hiding one another. An absent registry remains
allowed for standalone workflows; registration supplies its authoritative scope.

The editor contract lives at
`backend/src/Wf.Validation/Schemas/team.schema.json`, with a complete sample at
`backend/samples/workflows/teams/demo/team.json`. Tests pin schema properties and
required fields to the source-generated runtime contracts. The schema rejects
missing type names/owning teams; the shared validator additionally checks that
owning teams match, metadata constraints, quotas and near-duplicate names.
Optional `roleBindings` on the team and each type map
`Viewer`/`Operator`/`Author`/`Admin` to non-empty Entra `groupId` UUIDs. Omitted
maps are empty; duplicate role/group pairs within one scope fail, while one
group may hold different roles or appear at both scopes. Sample UUIDs are
placeholders, not deployed groups. Registration persists the group bindings in
the same transaction; scope-aware reads and mutation policies enforce the current
bindings through the shared authorization handler.

Migration `0015_team_type_registry.sql` adds `teams` and `workflow_types`.
`RegistryWriter.UpsertAsync(connection, transaction, registry)` uses the same
shared rules and trusted constructor quota (default 10), serializes updates per
team, and replaces the complete team/type snapshot in the caller's registration
transaction. It never commits: commit with all other registration work, or
roll back on any error. JSON-equivalent snapshots with unchanged bindings return
false without updating row versions or notifying; changed snapshots or repaired
bindings issue transaction-scoped
`NOTIFY registry`. Removed types are deleted, not silently retained; future
workflow foreign keys must prevent removal of in-use types.
IDs preserve exact declared names as `team/type`; shared validation and editor
schema now bound both segments to 128 Unicode code points and reject path
separators/control characters, avoiding ambiguous IDs and oversized index keys.
The authenticated catalogue exposes `GET /api/v1/teams`,
`GET /api/v1/teams/{team}/types` and `GET /api/v1/workflows`.
All three use current stored team/type grants through the same scope handler as
mutations and live subscriptions; Author and Operator independently inherit
Viewer. A type-only grant exposes its team container and permitted types, never
sibling types, role/group maps or team-wide presentation containing denied type
names. A team grant exposes its complete presentation; global Admin retains the
full registered team snapshot for administration.

Type rows include metadata schemas and counts of each workflow's latest durable
run status (`NeverRun` for no history). Workflow rows include active/latest
version identities, active-version metadata and a bounded latest-run summary,
not variables, manifests or blob capabilities. Deleted workflows are excluded.
Lists are uncached and scope-filtered again on every request, so registration
revocation needs no token renewal. Ungranted teams return 404 or an empty list.

Workflow lists accept exact `team` and `type` names (type requires team), `limit`
from 1 to 100 (default 50), and the previous page's opaque `cursor`. Paging uses
ordinal workflow identity, not offsets; latest runs use creation time then UUID
for deterministic ties. Invalid, duplicate and unsupported filters fail with
400 Problem Details instead of being silently ignored. Full run search/filter
and catalogue UI/live views remain their dependent tasks.

Migration `0016_role_bindings.sql` adds global/team/type scopes (0/1/2), shared
Viewer/Operator/Author/Admin values (0/1/2/3) and group/user principals (0/1).
`RoleBindingSync` reconciles validated team/type group maps inside
`RegistryWriter`'s transaction, deleting removed groups and removed-type maps.
Global grants, explicit user grants and unrelated teams are preserved.
Even an unchanged JSON snapshot reconciles missing bindings after an upgrade;
unchanged binding rows retain their UUIDs and row versions. Registry and role
changes become visible together only at commit, with no permission cache to
leave stale entries behind.

`AddTeamAuthorization` registers `Team.Viewer`/`Operator`/`Author`/`Admin`
policies. HTTP routes use `RequireTeamRole(role, resourceKind, routeParameter)`;
non-HTTP callers pass `TeamResource` to `IAuthorizationService`. Resources are
resolved from stored teams/types or the persisted run/series workflow identity,
never a caller's team metadata. Ambiguous or unresolved series fail closed.
Team grants include their types; type grants never grant team-wide visibility.
Author and Operator each include Viewer, but neither includes the other.
Scoped Admin includes every role only within its scope; only the exact Entra
app role `Admin` bypasses scoped grants. Database global bindings do not replace
that app role. Delegated users match `oid` user grants and `groups` group grants;
app-only, Agent/Registrar, malformed and group-overage claims cannot acquire
user-scoped roles. Group overage requires a fresh complete token; no Graph
expansion or permission cache is used.

Scoped HTTP denials return 403 `application/problem+json` with a sanitized
`detail` and `reason: insufficient_scope_role`; missing authentication remains
401. The initial `/hubs/live/v1` hub authorizes every `Subscribe(scope,id)` for
`team`, `type`, `run`, `series` and global-Admin-only `machines` (`id="*"`).
Subscribe returns null on success, otherwise a Problem Details object with
`status` 403 (or 400 for unsupported input): an upgraded SignalR invocation
cannot change its HTTP status. Denied subscriptions never join groups; a
denied re-subscribe removes earlier membership. UUID group IDs are canonical.
JWT query tokens are accepted only under the exact hub path segment, never on
REST routes or in preference to an Authorization header; connections close at
token expiration. MessagePack, event fan-out/coalescing, proactive revocation
of existing subscriptions, logs subscriptions and the separate browser-facing
hosting topology remain live/hosting tasks, not implied by this authorization
foundation.

Optional team `presentation` config supplies ordered `typeOrder`, `groupingKeys`
and metadata `listColumns`, plus metadata-keyed `defaultFilters` and display
`labels`; the demo `team.json` includes an example. Type names and metadata keys
are exact/ordinal. Partial type order is allowed without mutating registry
declaration order. Metadata references must exist in at least one declared
type's schema, even when free-form workflow metadata is allowed. A default
filter value must satisfy at least one declaring type's allowed values (or an
unconstrained declaration), allowing defaults to select a subset of types.
Unknown/repeated references, invalid filter values and empty labels return
WFV024 with precise paths but no echoed values. Missing/null presentation and
empty config preserve existing defaults. Shared validation and editor schema
cover the contract; applying it in team views remains a separate UI task.

`TypeNameSimilarity` supplies the shared registry normalization and bounded
Levenshtein helper: invariant lowercase letters/digits only, then remove one
trailing plural `s` when more than one character remains. `WithinTwoEdits`
compares already-normalized strings with ordinary insertion/deletion/substitution
distance <= 2; transposition counts as two edits. It checks a five-cell band per
row, observes cancellation and handles empty-string boundaries without
truncating long names. This is literal normalization, not linguistic stemming.

The hosted control plane acquires the `0009_leases.sql` engine lease, renews
every 3 seconds and expires after 10 seconds of database time. Each acquisition
has a monotonically increasing epoch. All hosted registry/run/dispatch/result
and group-commit transactions fence their captured epoch before persistence;
PostgreSQL bounds fenced transactions to the lesser of 1 second or remaining
lease lifetime, so a paused client cannot hold takeover indefinitely. A database
authority failure disables writes until reacquisition.
Standby acquisition retries every second; successful leader renewals remain
every three seconds. Graceful host shutdown stops producers, closes and drains
the group appender while renewal remains active, then stops renewal and releases
the lease. Hosted services stop sequentially in reverse registration order;
concurrent stop is explicitly disabled. A stopped lease manager cannot acquire
again. Real two-host/PostgreSQL handover measured <= 992.7 ms to the next leader
and <= 1,644.3 ms through recovery readiness across three stop phases, with all
16 buffered runs preserved and completed per phase. These healthy-storage
measurements do not override the existing bounded appender drain failure/timeout
behavior; hard-kill takeover still waits for lease expiry. Windows tests use
`IHost.StopAsync`, not an actual Unix SIGTERM signal.
`/healthz/leader` returns 503 on standby or until recovery, active versions and
the scheduler cursor snapshot are ready; gRPC sends `NotLeader` with the known
leader hint and uses the actual epoch on commands/ACKs. Configure `Engine:Port`
(default 5000) and `Engine:PublicEndpoint` for multiple instances. Standalone
persistence test/benchmark modules may omit the lease manager; hosted DI always
supplies it.

`0010_commands.sql` adds the durable trigger/abort inbox. `CommandInbox` accepts
preallocated UUIDv7 run IDs and preserves the first command of each kind per run.
`RunRecovery` drains pending commands transactionally on acquisition, restores
active queued/running/aborting snapshots plus their committed event tails, and
rebuilds `DeadlineTracker` entries from absolute run/node/attempt deadlines,
retry timestamps and persisted cancellation/kill phases. An outage never resets
the retry time or cancellation grace. Command effects and `processed_at` commit
together; failed or fenced recovery never publishes a ready cache. New commands
and directly committed scheduled runs are discovered while leadership remains
current, and reacquisition rebuilds the bootstrap state. `RecoveredRuns` supplies
this state and due-timer proposals to
the hosted orchestration actor; dispatch/cancel/kill effects are not executed by
the recovery loader. Full hosted workflow/agent coordination remains separate.
The event appender buffers transient database failures within its bounded queue
and retries after 100 ms up to 1 s, retaining the captured lease epoch. An
expired/superseded writer is rejected, never silently transferred to a new epoch.
A lost COMMIT acknowledgement is safe to retry: only an exact, full persisted
event replay succeeds; conflicting or partially replayed batches roll back.
Normal disposal drains; an outage lasting beyond the 5 s shutdown drain budget
fails remaining requests explicitly rather than hanging or acknowledging them.
The failover fixture kills a real leader process while a shared-core no-op run
is active; a second process acquires and completes it within 30 s. Disposable
PostgreSQL stop/start and dropped wire-level COMMIT acknowledgements are tested
separately. Shared external PostgreSQL instances are never stopped by tests.

`AgentLossSweeper` shares the core liveness policy: 15 s without a heartbeat
excludes an agent from placement, but its attempts remain reclaimable until
30 s after that original heartbeat. Engine recovery does not add another grace
period. At expiry, a fenced transaction classifies dispatched/running attempts
as `AgentLost`, records the stop time/reason and removes undelivered dispatches.
On reconnect, the gateway reconciles the full run/task/attempt identities in
`Hello.inflight` before acknowledging Hello; omitted attempts are lost immediately,
reported attempts remain reclaimable. Superseded sessions cannot reconcile.
Attempt classification and dispatch removal roll back together.

`0011_attempt_replacement.sql` retains the original dispatch, placement request
and resolved execution policy on each attempt. `Dispatcher.DispatchAsync` has
an overload accepting that policy; the original overload preserves default
timeouts and the placement request's `AutoRetry` setting. The hosted
`AgentLossHandler` reconstructs lost work from PostgreSQL, re-places on another
eligible agent with at most two platform retries, and never consumes author
retries. `NoAutoRetry`, exhausted budgets and elapsed run/node deadlines fail
the core task with an explicit stored reason. Missing legacy recovery inputs
fail safely rather than inventing a dispatch. No eligible agent leaves work
pending without spending budget. Retry events, attempt, dispatch outbox and
processing marker commit atomically; a failed transaction changes no placement
cache. A fresh attempt timeout is bounded by the original run/node deadlines.

Tests kill an actual agent during an SDK delegate, advance the virtual liveness
clock through the 30 s reclaim window, then execute the replacement successfully
on a second actual agent. This verifies recovery, not a strict wall-clock
heartbeat-to-completion limit. The plan's simultaneous 30 s reclaim window and
30 s total replacement/completion target conflict; timing sign-off remains
blocked pending clarification, without shortening the approved reclaim window.

Placement is authored at workflow, node or task level with `RunsOn` and
`Affinity`. The innermost selector replaces the whole outer selector; affinity
inherits independently. `RunsOn()` explicitly clears constraints; `Affinity.None`
clears an inherited pin. Stages support the same settings.

```csharp
w.Variables(v => v.StringList("machines", ["worker-a", "worker-b"]))
 .RunsOn(v => Machines.All(Os.Linux, Machines.Named(v["machines"]), Machines.Label("dotnet")))
 .Affinity(Affinity.Node)
 .Node("build", n => n.Task("compile", Compile,
     t => t.RunsOn(Machines.Pool("ci")).Affinity(Affinity.None)));
```

`Os.Windows/Linux`, `Machines.Pool`, `Machines.Label` (presence or exact value)
and `Machines.Named` compose with AND; names within one `Named` are alternatives.
Selector callbacks build symbolic metadata once. Describing never reads run
variables or invokes task delegates. Effective task selectors resolve from bound
string/enum variables at run start; machine names also accept string lists.
Missing/empty/secret/wrong-type values fail before execution. Described templates
stay unchanged between runs. Affinity supports `None`, `Node`, `Run` and
`SameMachineAs("node")`; unknown/self/deadlocking references are rejected.
In-process local execution binds selectors but does not create or dispatch to
named agents; machine matching belongs to distributed placement.

`Wf.Engine.Core.Placement.PlacementEngine` filters health, drain, selectors,
reservations, slots and RAM, then prefers warm hosts, lower slot utilization and
stable round-robin ties. `Propose` changes nothing; `Apply` follows the durable
dispatch event and rejects stale/foreign decisions. Capacity supplied by the
dispatcher must include committed dispatches not yet reflected in heartbeats.
Warm-package sets describe the bounded host pool, not every downloaded package.
Node references use the first committed placement; platform re-placement moves
affected pins without spending an author's retries, while `NoAutoRetry` blocks
platform placement. Snapshots restore pins and rotation; `ForgetRun` releases
terminal-run state. Placement timeout and API single-machine overrides follow
in their own backlog task.

## Blob storage

`IBlobStore.UploadAsync(content, source)` and `DownloadAsync(content, destination)`
stream with verified SHA-256 and exact length, using bounded buffers.
`BlobContent(kind, sha256, sizeBytes)` generates content-addressed keys; callers
cannot supply filesystem paths. Both stores default to a 5,000,000,000-byte
limit. Publication is atomic/no-overwrite; identical concurrent uploads are
idempotent, while corrupt existing content fails. Failed downloads may have
written untrusted partial data: discard it or abort an already-started HTTP
response rather than reporting success.

`FileSystemBlobStore` requires a service-private root and an external stable
HMAC key of at least 32 bytes. `CreateUploadGrant` returns a bearer token, not an
HTTP URL; `UploadWithGrantAsync` verifies the signed content/size/expiry and
streams to atomic publication. Grants last at most 15 minutes and deliberately
permit identical immutable-content retries until expiry. Never log grants or
keys. Reparse paths are rejected; filesystem ACLs must exclude untrusted writers.

`AzureBlobStore` uses Azure.Storage.Blobs 12.30.1 and a configured
`BlobServiceClient` with production Entra credentials or emulator SharedKey.
Provision a private container externally. `CreateUploadGrantAsync` issues a
short-lived, create-only SAS for a random staging object; expose only its URL,
maximum bytes and expiry, retaining the grant descriptor as trusted server state
on the same singleton. `CompleteUploadAsync` verifies actual bytes (not supplied
SHA metadata) and a pinned ETag before final publication. Never reconstruct the
descriptor from untrusted completion JSON. Direct SAS cannot enforce an ingress
byte cap; completion enforces it, or use mediated `UploadAsync` for a strict cap.
Create-only SAS requires a single raw Put Blob, not an SDK block-upload fallback;
grants larger than the provider's 5,000 MiB single-PUT limit are rejected.

Configure cleanup of abandoned staging objects/files after crashes or unused
expired grants. Actual filesystem/Azurite tests cover streaming, cancellation,
checksum/size failures, atomicity, grant scope and path safety. Live cloud managed
identity and 5 GB throughput are not claimed. Report routes and complete
application-profile configuration remain their separate tasks.

## SDK artifacts

Dispatched SDK tasks publish with
`await ctx.Artifacts.PublishAsync(name, stream, contentType, ctx.CancellationToken)`.
The returned URI is a Viewer-authorized download route, not a storage capability.
The caller retains ownership of its stream. Seekable inputs are hashed from their
current position, rewound, then streamed; non-seekable inputs are hashed into a
private, delete-on-close `.wf-artifact-*` file in the task host's working directory.
That directory must be writable and service-private. Neither path buffers the
artifact in memory. Names are single filenames (≤ 255 UTF-8 bytes), content types
are bounded media types without parameters, and SHA-256/length are verified by
the existing `IBlobStore`. Empty artifacts are valid.

The gateway supplies an `artifacts` capability bound to run, exact task attempt,
owning agent, current agent session, reservation epoch and attempt deadline. The
agent restricts its destination to configured engine origins; the SDK uses exact
HTTP/2 without redirects and never places capabilities in URLs. Upload admission
and completion both recheck durable ownership/fencing. Aborted/lost/replaced
attempts cannot create new artifacts; an exact committed replay remains
idempotent while its original capability and agent session remain valid.

`POST /api/v1/artifact-grants/{id}` accepts
`{name,contentType,sizeBytes,sha256}` using an Authorization bearer capability and
a canonical UUIDv7 ID. It returns `201 {artifactId,expiresAt,committed}`. PostgreSQL
reserves the exact byte count before any upload: committed bytes plus all live
reservations cannot exceed **5,000,000,000 bytes per run**. Same-ID/same-metadata
retries consume no additional budget; changed metadata conflicts. Reservations
last at most 15 minutes. `PUT /api/v1/artifact-publishes/{id}` streams raw
`application/octet-stream` bytes with the exact Content-Length and the same
capability, then returns `201 {runId,artifactId}` only after verified storage and
metadata commit. A per-run advisory lock keeps an active upload counted even
across grant expiry; parallel grants wait rather than oversubscribe. No engine
run/attempt row lock is held while streaming.

`DELETE /api/v1/artifact-grants/{id}` releases only the caller's uncommitted
reservation; the SDK attempts this on upload failure/cancellation. Expired unused
reservations are reclaimed during the next grant. Failed streams cannot create
committed rows. A verified blob followed by a database/fencing failure can leave
unreferenced immutable content; retention must collect such objects, as for
reports. Committed metadata contains source, name, type, byte count, SHA-256 and
an `artifacts/{sha256}` pointer, never payload bytes.

`GET /api/v1/runs/{id}/artifacts` returns committed artifact summaries.
`GET /api/v1/artifacts/{id}/content` rechecks fresh run-scoped Viewer permission
and streams verified bytes as `application/octet-stream` attachment with
`nosniff`, `no-store` and restrictive CSP; artifacts are never inline reports.
Late checksum/read failures abort the response rather than completing partial
content. User tokens cannot mint upload grants, and attempt capabilities cannot
download artifacts. Budget failures raise nonretryable
`ArtifactLimitExceededException` through the ordinary task failure policy.

These routes use the existing configured/injected blob store and upload HMAC
key; the default profile remains filesystem-backed. Tests exercise small real
payloads through PostgreSQL, filesystem, authenticated HTTP and dispatched SDK
child processes, plus synthetic exact 5 GB reservation arithmetic. The separate
task 9.2.1.1.3 owns full 5 GB roundtrips, cross-OS RAM and Azure throughput gates.

## Registration

The actual host exposes loopback HTTP/1.1 REST/SignalR on `Api:Port` (default
5001), beside the unchanged HTTP/2 agent/engine listener on `Engine:Port`
(default 5000). Ports must differ. Configure `Storage__UploadHmacKey` with an
externally generated, stable base64 key containing at least 32 random bytes;
missing, malformed or short keys fail startup without echoing key material.
Use the same protected key across API replicas/restarts. `Storage__Root`
defaults to `wf-data/blobs` under the executable directory; configure a
service-private, writable shared root explicitly outside isolated local use.
Migration mode still needs neither authentication nor an upload key.

`POST /api/v1/upload-grants` requires the app-only `Registrar` policy and
accepts `{kind:"package",sha256,size}` (1 byte to 512 MiB).
A verified existing blob returns `200 {exists:true}`; otherwise 201 supplies
a relative `uploadUri`, exact `maxBytes`, and expiry. PUT raw bytes to that
URI without an Authorization header. Its HMAC bearer capability binds kind,
digest, exact size and expiry, permits identical retries for ten minutes,
and never authorizes arbitrary paths or content. Known and chunked oversized
bodies return 413; short/wrong-hash bodies return 400; invalid/expired grants
return 403. Partial uploads are never published. `BlobUploadGrants` is shared
with the filesystem adapter; mediated uploads also work with an injected
`AzureBlobStore` without process-local SAS completion state. The default host
uses the filesystem store; Azure profile/credential wiring follows separately.
Never log upload URLs, grants or HMAC keys.
The host restricts raw ASP.NET Core request/routing logs to Warning so
capabilities in paths and live access-token query strings are not logged.

`POST /api/v1/registrations` also requires app-only `Registrar`. The strict
contract requires `gitCommit` (40 or 64 lowercase hex characters) and
`gitAuthor`; optional `changeNote` is limited to 4000 characters. A workflow
registration supplies `workflowId` (`team/type/name`), `manifest`, package
`{sha256,size}` and the single-file `source`. Manifest `name` must equal the
complete workflow ID and metadata `type` its type segment. Include the optional
complete `registry` team snapshot, or register the team/type first. Registry-only
requests omit workflow fields. Unknown/duplicate fields, missing/null mandatory
values and malformed contracts fail explicitly; JSON body limits are 8 MiB for
registrations and 4 KiB for grant issuance. Non-JSON content types return 415.

The service runs shared DAG/variable/HTTP/policy/type-metadata/SemVer checks.
After raw-manifest preflight and strict contract/envelope checks, the non-compiling
`Validator.Validate(manifest, schedules, registry)` backstop aggregates supplied
schedule and registry errors in offline order. Invalid raw-manifest semantics
return before companion checks, matching offline description preflight.
400 Problem Details `errors[]` preserve every shared rule ID, file, path, message
and hint; use the same logical file paths when comparing offline and server
results. Request/provenance/ownership/package/policy failures remain registration
envelope diagnostics. Authoritative stored registry, retained schedules and
version compatibility are rechecked under the registration transaction.
The service verifies actual stored package bytes and archives at most 1 MiB of UTF-8 source
as a deterministic `workflow.cs` ZIP. Source keys follow the blob store's
`sources/{sha256}` convention, not user paths. Uploaded immutable source blobs
can remain unreferenced after a rejected transaction; clean those up through
the later storage lifecycle policy.

Migration `0017` adds workflow/version persistence, registry history and the
registration audit foundation. Team-scoped transaction locking makes version
insertion, active/latest pointers, registry/group replacement, history, audit
and `NOTIFY registry` atomic. Versions record source/package hashes and sizes,
SDK version, Git commit/author, change note and the authenticated principal's
`oid`, never a supplied registering identity. Audit summaries retain pointers
and registry fingerprints, not source or variable values. Failure to audit
rolls back the mutation. In-use workflow types cannot be removed by a registry
replacement.

SemVer text preserves prereleases, build metadata and large numeric components,
with a 256-character API/storage bound; build metadata cannot create a second
identity. Reusing a version with different immutable content returns 409.
New versions must satisfy the shared classifier against the latest registered
version, even if an administrator later rolls the active pointer back; 409
responses preserve WFV010 diagnostics/hints. Identical requests return 200 with
no rewrites, history or audit duplication and never reactivate an old version.
201 responses include only changed `workflowVersion`, `registrySnapshot` and/or
`scheduleConfigVersion` UUIDs. Existing runs retain their version IDs.

Schedule-only registration supplies `workflowId`, `schedules` (a string containing
the complete original JSON file), `scheduleSourcePath`, and Git provenance;
omit manifest/package/source. Paths are bounded repository-relative logical
paths using `/`, without dot segments, backslashes or control characters.
The file is limited to 1 MiB of valid UTF-8. Schedule-only changes leave workflow
versions untouched and return only `scheduleConfigVersion`. Code keys keep their
schedule UUID and permanent series through edits, removal and re-addition;
removal soft-deletes/pauses the schedule and clears its cursor, preserving fires
and run history. UI schedules remain untouched. An unchanged expression retains
its existing due cursor and pause state; a changed expression or revived
instance gets a new cursor from the registration clock. Identical content
returns 200 without rewrites; reverting to known content reuses its historical
config UUID and reactivates that snapshot. Provenance remains that of the first
registration of those exact bytes. New workflow registrations also revalidate
the retained schedule file against the active workflow's variables and refresh
effective defaults, without creating a schedule config for unchanged file bytes.
Every file/config/series mutation shares the registry/version/audit/NOTIFY
transaction. Run-now and recurring fires resolve retained version pins and
effective variables; apply-under-load firing verification is separate.

`ActiveVersionCache` is a hosted service shared by future trigger handlers.
It listens to `registry` before loading the initial atomic immutable snapshot,
refreshes after committed notifications without restarting the engine, and
resynchronizes completely after reconnect. Idle listener heartbeats detect
broken connections; transient database failures log an error and make new
lookups unavailable until resynchronized. Corrupt stored identity/manifest data
fails explicitly instead of returning a stale or partial snapshot. Workflows
without an active version, or soft-deleted workflows, are excluded.
Callers capture one `ActiveWorkflowVersion` and use its immutable version ID,
manifest and package for a new run/command; later activation does not retarget
that captured value or existing persisted run IDs. Leader readiness requires
the cache to be synchronized. Trigger HTTP wiring and the 20-in-flight-run hot
activation scenario remain their own tasks.

Publish CLI/merge automation, workflow/version detail reads, proactive live revocation,
general audit middleware/partition maintenance and
cloud profiles remain their dependent tasks.

## Agent

The Native AOT agent runs in a console, as Windows Service `wf-agent`, or under
systemd. Service lifetime integration, installers and the persistent engine
registry are implemented.

```powershell
$env:Agent__Engines__0 = "http://localhost:5000"
$env:Agent__Id = "f2111fd3-f78e-455b-bbe0-75aeae01fdd1"
$env:Agent__Labels__dotnet = "10"
dotnet run --project backend\src\Wf.Agent
dotnet publish backend\src\Wf.Agent -c Release -r win-x64
```

Publish `linux-x64` on Linux with the Native AOT compiler prerequisites; Windows
and Linux CI publish native executables and connect each to a real HTTP/2 test
gateway. Run the native smoke test locally by setting `WF_AGENT_EXECUTABLE` to
the published executable before `dotnet test`.

`Agent__Engines__1`, etc. configure additional engine origins. Optional settings:
`Agent__Name`, `Agent__Pool` (default `default`), `Agent__MaxTasks` (default CPU
count), `Agent__Labels__<key>`. `Agent__Id` is a required stable UUID; generate
it once and retain it across restarts. Names default to the machine name and
must be unique in the registry. Hello reports OS, architecture,
CPU count, physical RAM, labels and binary version. Five-second heartbeats report
machine CPU/RAM/disk and slots. Disconnects retry configured engines within one
second; leader hints are followed only when they match a configured endpoint.

`CapacityProfile` identifies measured/manual requests-per-second capacity by
transport, codec, payload-size class and ordinally sorted string load settings.
Its source-generated compact JSON `ProfileKey` preserves exact identifier/value
case and escaping; settings order does not change identity. Keys are bounded to
2,000 UTF-8 bytes for the PostgreSQL composite index. `AgentCapacity` requires a
positive whole-number rate; only Calibrated values carry a UTC calibration time.
Migration `0012_agent_capacities.sql` adds per-agent/profile rows with foreign-key,
source and timestamp checks without changing existing agents. No row means
unverified, not zero capacity. Manual override APIs and calibration dispatch
remain separate tasks; no Bond codec profiles or load executor are included.

HTTPS is required off loopback. `Agent__AllowInsecureTransport=true` is only for
an isolated internal container network; TLS certificate validation stays enabled.
Agent-role token acquisition is a separate authentication backlog item.
Dispatch and cancel commands run asynchronously, so downloads and user tasks do
not block heartbeats or stream reception. Replayed attempts are deduplicated
within the agent process. Started/completed messages retain their sequence until
acknowledged and replay on reconnect or process restart. Production uses an
owner-only, checksummed result journal under the user's local application data
`wf\agents\<agent-UUID>\results`; override with `Agent__ResultJournalDirectory`.
Atomic flushed snapshots retain sequence high-water marks, observed leader epoch
and unACKed results before send; completion is durable before the task host ACK.
Only a valid current-connection/current-leader ACK removes offered records.
Limits are 4,096 records, 1 MiB per record and 32 MiB total; corruption, disk
failure and exhausted capacity fail explicitly rather than dropping results.
Hello includes both running attempts and every unACKed result, even when this
logical backlog exceeds physical task slots. The registry bounds it to 8,192
attempts and a 1 MiB protocol frame; physical slots remain clamped to MaxTasks
until a heartbeat reports actual usage. Replayed terminal results are ACKed
without a second event or changed outputs. Drain and live-host re-adoption
remain separate tasks.

`TaskHostPool` prewarms compiled workflow processes and verifies their PID,
protocol and workflow version over owner-only Unix domain sockets on Windows
and Linux. Warm dispatch starts no process. Each host binds once to one attempt:
parallel tasks cannot share mutable memory, limits or forced cancellation.
The SDK server executes real registry delegates with typed variables, declared
inputs and bounded outputs; completion waits for acknowledgement before exit.
Cancellation is cooperative first, then kills that host's process tree after
grace (default 30 seconds). Workflow hosts use ordinary, untrimmed .NET IL, not
Native AOT, and require the ASP.NET Core runtime. Resource-limit enforcement and
live-host re-adoption remain separate backlog items. The sample accepts
`--task-host --socket <path>`; normal local execution is unchanged.

The global warm pool defaults to four unused processes and 200,000,000 bytes
aggregate RSS, evicting least recently used hosts and emptying after five
monotonic idle minutes. Active hosts are not warm-eviction candidates.
Eviction waits for process exit before admission; package pins survive disposal
until exit. RSS is checked on admission and sampled while warm hosts exist, not
enforced as an instantaneous OS hard limit. SDK private IPC startup uses only
Kestrel/routing/gRPC services; agents retain Native AOT size optimization.

Footprint acceptance remains **blocked**, not complete: final Windows native
cold-dispatch peaks include simultaneous agent growth above its idle baseline
plus the full real SDK task-host RSS. MaxTasks=1 measured 53,555,200 bytes/task
(51.0742 MiB); MaxTasks=2 measured 51,558,400 bytes/task (49.1699 MiB).
Both exceed the contractual 50,000,000 bytes/task. Native idle measured 0.0244%
normalized CPU over 20 s and about 26 MB RSS, below 1%/100 MB.
Set `WF_AGENT_EXECUTABLE` to a published native agent and
`WF_AGENT_FOOTPRINT_BENCHMARK=true` to enable the strict `FootprintBenchmark`
integration lane; it intentionally fails until the combined cold budget is met.
No managed-runtime subtraction, forced GC or working-set purge is used.
Linux measurements remain outstanding.

`PackageCache` downloads immutable ZIP packages into SHA-256-addressed directories.
Set `Agent__PackageCache` to an absolute path to override the user's local
`wf-agent\packages` directory. Packages contain published IL/dependencies plus
`wf-package.json` with `version` and root-level `entryAssembly` DLL fields.
HTTPS is required except on loopback. Hash, path, link, size and manifest checks
precede atomic publication; warm hits verify archive and extracted-file integrity
without HTTP or compilation. Cache failures are explicit attempt platform errors.
Windows publication retries only bounded file-lock/access conflicts.

Published cache storage is bounded to 4 GiB and 32 packages by default, counting
archives plus expanded files. `PackageCache` constructor budgets can override
these positive limits. Successful cold/warm reads durably touch LRU ordering;
oldest unused packages are evicted first, including after restart. Use
`using var package = await cache.GetAsync(...)`: each returned `TaskHostPackage`
owns a caller lease, and every warm/active host independently pins its package
until process exit. Production dispatch already disposes its caller lease.
Pinned capacity or an oversized package fails explicitly without partial
capacity-plan eviction; corruption and filesystem failures are not cache misses.
Use one cache owner per root. Limits exclude foreign content, abandoned staging,
filesystem allocation overhead and one serialized in-progress download/extraction.
Persisted host pins across agent restart belong to live-host re-adoption.

`Dispatcher` accepts only database-committed Ready tasks in running runs.
Dispatched event, attempt capacity reservation and dispatch outbox commit in one
PostgreSQL transaction before the gateway sends anything. Committed pending
attempts count against slots/RAM before heartbeats catch up. Streams have one
writer; a new Hello fences the old session, pending commands replay, and task
events are acknowledged only after durable, idempotent acceptance. Core readiness
and node/run coordination are explicit caller responsibilities; this is not yet
the full hosted run actor or HA leader loop.

The control-plane agent gateway listens on loopback HTTP/2 port 5000. Configure
`ConnectionStrings__Workflow` and authentication as above, run the explicit
`--migrate` deployment mode, then run `dotnet run --project
backend\src\Wf.ControlPlane` (with `-- --dev` only in isolated Development).
This is not yet the full hosted run-actor/scope-RBAC control plane.
Registration and heartbeat acknowledgements
follow durable writes. Agents lose health at exactly 15 seconds without a
heartbeat; dispatch eligibility checks the deadline even before the sweeper runs.
Reconnecting Hello replaces the session token, fencing old connections.

`infra\agent\install-agent.ps1` installs a Native AOT executable and persistent
UUID/configuration as a Windows LocalService service (elevated PowerShell).
`-WhatIf` validates inputs without changes. On Linux, run
`sudo bash infra/agent/install-agent.sh --executable <published-path> --engine
<origin>` to install the restricted `wf-agent` system user and systemd unit;
`--validate-only` makes no changes. Both refuse to overwrite an existing install.
CI installs, starts, checks and removes services on its disposable runners.

## Schedule expressions

`Wf.Validation.Schedules.ScheduleExpression.Create(cron, interval, timeZoneId)`
accepts exactly one standard five-field cron expression (or supported macro) or
positive elapsed interval, plus an explicit IANA timezone. UTC results are
strictly after the supplied cursor; `GetNextOccurrences` returns an immutable
preview (five by default). Cron uses Cronos DST semantics: fixed times in a
spring gap move to the first valid instant, fixed fall-fold times fire once,
and interval-like cron preserves distinct UTC occurrences across both offsets.
Elapsed intervals must advance from the last intended fire, not scheduler poll
time. Hosts need ICU/timezone data. The hosted scheduler uses this same expression
implementation for persisted cron and interval cursors.

`Wf.Engine.Core.Scheduling.OverlapPolicy.Decide(activeRuns, policy)` reuses the
schedule contract's Skip/Queue/Concurrent values. With no other nonterminal run
in the same series it returns Start; otherwise default Skip, Queue, or Start
for Concurrent. Count queued runs too, excluding the candidate itself.
Decisions are pure and reject invalid policies/counts. The hosted scheduler
records skipped fires transactionally; series UI and FIFO queued-run release
remain separate tasks.

`Wf.Engine.Core.Scheduling.SchedulerLoop` plans from a persisted next-fire cursor,
a next-occurrence delegate (for example `ScheduleExpression.GetNextOccurrence`),
an explicit nonnegative grace period and a `TimeProvider`. Every slot older than
grace remains `Missed`; slots exactly at grace remain due for a normal start.
Default Latest also emits one separate `CaughtUp` run action, trigger `catch-up`,
referencing the newest missed slot. None suppresses only that action, not history.
Never insert a second `schedule_fires` row for the catch-up action: retain the
missed slot's identity and link the run to its source. The returned next cursor
preserves interval phase and UTC/DST identity. Commit cursor advancement,
missed history and run creation atomically; repeated planning alone is not
exactly-once execution. Applying this beyond-grace policy in the hosted scheduler
uses `ScheduleCatchUp` and the same pure planner.

Migration `0014_schedule_fires.sql` stores fire history with an exact-once
`(schedule_id, fire_at)` key; claim with `ON CONFLICT DO NOTHING` and create a
run only when the insert succeeds in the same transaction. Stable outcomes:
Started=0, Skipped=1, Missed=2, CaughtUp=3. History references existing schedules
(soft-delete them); optional run IDs remain history references after run
retention removes the corresponding run. Timestamps must be finite, and all
callers must use PostgreSQL's microsecond precision for slot identity.

`ScheduleFireService` owns the leader's `ScheduleTimerHeap`. Each due candidate
rechecks the current unpaused/non-deleted schedule under a row lock and fences
the lease inside the same transaction as the fire claim, overlap decision,
initial run/snapshot/node rows and next intended cursor. Scheduled runs retain
the permanent series, selected version UUID, metadata and bound effective
variables; `trigger=Schedule`, `triggered_by=scheduler`. Replayed slots never
replace fire history or create another run. Cursor advancement does not change
the configuration's optimistic row version.

The service listens to `registry` and `schedules` before taking its snapshot,
reloads after committed notifications and on takeover/reconnect, and refreshes
every five seconds as a backstop. Edits are read again at admission, not copied
from stale heap entries. Shared `WorkflowVersionResolver` handles trigger/run-now
and recurring exact/range pins; a recurring unpinned fire reads the current
active pointer in its transaction, independently of notification ordering.
Invalid pins/values log a rejection and retain the due cursor, without starving
other schedules. Transient storage failures disable scheduler readiness until
resynchronization; fatal corrupt state fails explicitly. Queued-run FIFO release
remains a dependent task, not implied by basic exactly-once firing.

The nightly/manual `schedule-chaos.yml` lane runs the actual one-hour
`ScheduleExactlyOnce` gate against PostgreSQL 17 with durability enabled:
1,000 minutely schedules, uniformly phased 60 ms apart across each minute,
and two hard leader-process kills during firing. The measured local gate
retained all 60,000 intended fire slots and runs, zero duplicate or invalid
runs, zero extra slots and zero silent misses. Intended-fire-to-durable-commit
p99 was 0.274046 s against the unchanged 2 s limit; maximum failover delay
was 15.887371 s, with 428 late slots recovered and lease epochs 1 -> 2 -> 3.
Commit timing uses PostgreSQL `track_commit_timestamp`; `fsync`,
`full_page_writes` and `synchronous_commit` stay on. Nightly artifacts retain
metrics, process logs and TRX for 14 days. This workload does not claim a
synchronized 1,000-at-once burst meets 2 s; development bursts measured p99
5-6 s. Hosted GitHub Actions execution remains unclaimed until a push.

Every overdue cursor is replanned against one UTC clock snapshot, including on
takeover and reconnect. `Scheduler:FireGrace` is a nonnegative `TimeSpan`
(environment: `Scheduler__FireGrace`), default `00:00:02`, matching the on-time
delay budget. Exactly at grace is an ordinary scheduled fire; older slots are
all recorded as Missed. Default Latest admits one catch-up action from the
newest newly claimed missed source; None records the same history without that
action. The missed row keeps outcome Missed and links its catch-up run with
`run_id` and reason `catch-up`, never a duplicate slot or rewritten outcome.
Stable `RunTrigger` values are Manual=0, Schedule=1, CatchUp=2.

Catch-up admission precedes recent ordinary due slots in the same transaction
and obeys Skip/Queue/Concurrent. An already-active series can suppress catch-up
under Skip; its missed source retains reason `overlap`. A catch-up admitted under
Skip can similarly suppress a newer ordinary slot, which is recorded Skipped.
All missing history, admitted runs and the final future cursor commit together,
so failure/retry or repeated takeover cannot consume only part of the backlog.
Hosted outage tests advance an injected clock, not a wall-clock sleep. The
multi-schedule case stops the actual hosted services, advances ten minutes plus
three seconds beyond the fire grace, and restarts across three leaders: each of
three Latest schedules records ten exact missed slots and one newest-source
catch-up run; two None schedules record those same misses without a run.
Paused/removed controls remain untouched, stale timers do not recreate work,
and the next leader recovers the persisted catch-up runs. Existing migration
`0007_schedules.sql` already supplies the canonical `catchup_policy` column;
no duplicate `catch_up_policy` or follow-up column migration is needed.
Series history displays a Catch-up trigger badge for its linked durable
`catch-up` source, without relabeling that source's Missed outcome.

`Wf.Agent.Secrets.SecretMasker` takes resolved secret values, never reference
names. `Redact` masks complete text; create a separate text stream for each
attempt/stdout/stderr, append decoded chunks and publish `Complete()` before
EOF. Streaming retains at most the longest value minus one UTF-16 characters,
preventing chunk boundaries from exposing matches; overlapping/adjacent matches
collapse to `***`. Use a stateful UTF-8 decoder before supplying byte chunks.
Matching is literal, ordinal and case-sensitive for each value and its standard
UTF-8 base64 form; arbitrary transformations, URL encoding and unpadded/base64url
forms are not detected. Lookahead includes the encoded values.
`RedactJson` masks decoded string values and property names while
keeping JSON valid; matching non-string scalars become strings. Key collisions
fail explicitly rather than discard outputs. Secret resolution, production
log/output transport wiring and stored-data masking verification remain
dependent tasks; existing transport paths are not yet protected by this helper.

## Workflow project template

Pack `backend\src\Wf.Templates\Wf.Templates.csproj`, install the resulting NuGet
package with `dotnet new install <package-path>`, then generate a workflow:

```powershell
dotnet new wf-workflow -n Example --sdkVersion 1.0.0
dotnet test
```

The configured NuGet source must provide matching `Wf.Sdk` and `Wf.Sdk.Testing`
packages. The template includes `workflow.cs`, `schedules.json`,
`workflow.api.json`, a local schedule schema and four runnable dry-run tests.
Generated tests use one assembly fixture and the built-in Microsoft Testing
Platform MSBuild integration, preserving the real out-of-process workflow
harness without VSTest startup overhead. Workflow compilation uses ordinary IL.

Isolated install/generate tests prepare actual SDK NuGet packages and enforce
the exact warm generated `dotnet test` command below 10 s, including restore/build
checks and unchanged workflow-cache integrity. Local Windows measurement:
5.916 s warm, 25.320 s with an empty isolated package cache; package/feed preparation
16.547 s separately. Cold download/compilation is not claimed below 10 s.
Windows/Linux CI runs the same integration; local Linux results remain unverified.

The same package includes `dotnet new wf-repo` (`--cliVersion` defaults to
`1.0.0`), which scaffolds a workflows-repository PR gate and local `wf` tool
manifest. Require the **Workflow PR gate** check. It selects changed workflow
directories from the full base/head diff, then runs actual `wf validate` and
`dotnet test`; validation, test and restore failures fail the check. Shared
changes select affected workflows conservatively; deletions, renames and
special-character file names are covered by selection tests.

The repository needs .NET 10, Python 3.9+, full Git history and readable NuGet
sources for tool package `wf` and pinned SDK/testing dependencies. No repository
variables or secrets are required; do not expose feed credentials to PR code.
From a generated repository, run the same gate locally:

```powershell
$env:PR_BASE_SHA = '<full base SHA>'
$env:PR_HEAD_SHA = '<full PR head SHA>'
$env:PR_CHECKOUT_SHA = git rev-parse HEAD
python .github\scripts\pr_gate.py
```

`--list` prints selection-only JSON. Local integration verifies valid, invalid
DAG, failing-test and restore-failure outcomes against real generated workflows.
Windows MSBuild currently rejects apostrophe/ampersand directory names
(`MSB3094`); selection handles them safely, while executable directory scenarios
cover Unicode and spaces. Hosted required-check behavior remains unverified
until a permitted push/PR.

## CLI and packages

```powershell
dotnet run --project backend\src\Wf.Cli -- abort $runId --reason "Stop deployment" --engine https://engine.example
```

`wf abort` requires an exact UUIDv7 and a nonblank 1-500-character reason.
`--engine` or `WF_ENGINE_URL` supplies an HTTPS origin; HTTP is restricted to
loopback. Supply the API bearer token through `WF_ACCESS_TOKEN`, not a command
argument. A non-loopback target requires it; token acquisition/refresh is not
implemented by this command. Loopback without a token works only when the
server itself permits it, such as explicit local development mode.

The official SignalR JSON/WebSocket client subscribes to the authorized run
before sending one public abort request with source CLI. Output includes the
engine's immutable accepted record and actual live run states, including
Aborting/Aborted, rather than presenting a 202 response as completed
cancellation. An existing first abort retains its original actor/source.
There is no REST polling or automatic abort retry. Redirects are disabled;
missing/oversized/malformed acceptance, live gaps/resync and transport loss fail
explicitly without claiming that durable acceptance was undone.

Exit codes: 0 only after observing Aborted; 1 for denied/rejected acceptance or
another terminal state; 2 for usage/configuration/transport/contract failure;
130 for Ctrl+C. Cancelling monitoring does not cancel an already accepted abort.
Actual-process tests observe Aborting, hard-kill the owning leader while the
controlled task remains running, then observe Aborted under the next leader,
with one original command, audit and abort-bearing event. This proves
lease/command/replay recovery through real PostgreSQL, not surviving arbitrary
external task processes or undoing completed external side effects.

```powershell
dotnet run --project backend\src\Wf.Cli -- validate C:\workflows\team\example --package C:\artifacts\example.zip
```

The directory must contain `workflow.cs`. `wf validate` reuses the shared
validator, including schedules, team registry and optional API-baseline checks.
Use repeated `--input <file-or-directory>` for dependencies outside the source
root, `--baseline <workflow.api.json>` for compatibility validation, and
`--work-directory <directory>` for build-cache artifacts. Exit codes: 0 valid,
1 validation/compiler/describe failure, 2 usage/infrastructure/integrity failure,
130 cancellation. Diagnostics retain rule, file, JSON path, message and hint;
compiler diagnostics remain visible.

`--team-type-quota <positive-integer>` overrides the trusted default of 10 types
per team; quota violations return WFV017 naming the configured limit. The same
option is `ValidationOptions.TypeQuota`, passed as trailing `options` to both
sync/async `Validator` and `WorkflowValidation` entry points. Invalid limits
fail before workflow discovery. `team.json` cannot raise its own quota;
author-provided quota properties are rejected as unknown contract fields.

`--package` emits a sorted ZIP with fixed entry metadata, every published file
and `wf-package.json` containing the workflow version and actual entry assembly.
The command prints the actual ZIP's SHA-256. Writes replace atomically; unsafe
paths, links, inventory changes and agent package-limit violations fail without
destroying an existing package. Compilation disables debug symbols to remove
random intermediate PDB-path changes from assemblies. Reproducibility requires
the same SDK/OS, source paths/environment, pinned dependencies, complete declared
inputs and deterministic workflow code. Tests compare two independent cold
builds byte-for-byte and execute the package through the real agent cache and
warm task hosts. Publishing/registration commands remain separate tasks.

## Script build cache spike

`Wf.Validation.Build.ScriptBuildCache.BuildAsync(new ScriptBuildRequest(script,
sourceRoot, additionalInputs), token)` publishes framework-dependent IL once,
runs the compiled `--describe` entrypoint, and returns the typed manifest, exact
JSON, assembly and publish paths. Content/configuration/dependency fingerprints,
cross-process locking, atomic publication and an integrity inventory protect
cache hits; corruption, source changes, compiler/describe failure and cancellation
fail explicitly, never serve stale success. Callers must declare external
dependencies; floating feeds, undeclared files and nondeterministic descriptions
are not tracked. No eviction, sandbox or implicit timeout is provided by this spike.

Local R-5 measurements: .NET SDK 10.0.402, Windows x64, real 20-node SDK workflow
plus schedule JSON, cached NuGet, concurrent shared-machine work. Totals for cache
operations include fingerprinting and integrity verification; not a full
Validator CLI benchmark.

| Path | Publish | Describe | Total |
|------|---------|----------|-------|
| Raw cold | 37.550 s | 0.679 s | 38.229 s |
| Raw incremental warm | 7.996 s | 0.572 s | 8.569 s |
| Cache miss | 31.744 s | 0.948 s | 35.523 s |
| Cache hit | skipped | skipped | 1.050 s |

The warm < 10 s gate passes. Twenty-six cache cases include real compilation,
schedule invalidation, concurrent builders, corruption and owned process-tree
cancellation. Timing evidence is kept here until a PR is explicitly requested.

`Wf.Validation.Validator.Validate(directory)` and `ValidateAsync` use this
cache for a directory containing `workflow.cs`. `Wf.Sdk.Testing.WorkflowValidation`
exposes the same API to xUnit and other callers. Results contain immutable errors
with rule ID, file, path, message and hint, plus the successful build or original
compiler/describe diagnostics. WFV001-WFV004 cover missing directory/script,
publish failure and describe failure. WFV005-WFV009 cover DAG cycles, unknown
dependencies, duplicate keys, variable type/default/allowed-value constraints and
required-flag shape. Raw manifest checks precede typed contract construction on
both fresh and cached descriptions; errors sort by rule ID and indexed JSON path.
Required variables without defaults are valid definitions; runtime binding checks
their supplied values. Cancellation, infrastructure/cache errors and malformed
description contracts propagate, not turn into validation success.
Directory validation executes trusted workflow code; it is not a sandbox.
The typed `Validator.Validate(manifest, schedules, registry)` overload instead
validates an already-described manifest and optional UTF-8 companion files
without building or executing source; registration uses this backstop. Optional
schedule and registry errors follow manifest errors; version comparisons remain
a separate step.

`dotnet test backend/tests/Wf.Validation.Tests --filter RuleCatalogCoverage`
requires an unskipped offline positive and negative test for every shared
`RuleCatalog` WFV identifier. Add `RuleCoverage(id, valid)` markers to actual
xUnit cases when introducing a rule; the guard rejects missing directions,
unknown markers and newly catalogued rules without evidence. It also executes
the marked real SDK publish/describe success and compiler/definition failures.
Registration-only envelope diagnostics such as REG001 are not shared rules.

`Wf.Validation.SemVer.ApiBaseline.FromManifest` generates the immutable
`workflow.api.json` contract: SemVer version, variable schemas and node keys.
Ordinal ordering and source-generated JSON make equivalent manifests produce
identical bytes. Strict parsing, stream read/write, cancellation and sanitized
errors are covered, including a real SDK round-trip with reflection disabled.
`BreakingChangeClassifier.Classify(previous, current)` returns required bump,
deterministic changes and WFV010 errors with the required level in their hints.
Removals/renames/type changes, narrowed constraints, changed defaults and newly
required values without defaults require MAJOR; compatible additions/loosening
require MINOR; implementation-only changes require PATCH. SemVer precedence
ignores build metadata; same-core prerelease progression satisfies PATCH only.
This is an explicit baseline-comparison step; registration wiring and schedule
compatibility follow separately.

## Benchmarks

```powershell
dotnet run -c Release --project backend\tests\Wf.Benchmarks -- --filter '*Validator*'
```

Full cached validation checks an actual 200-node workflow, 50 schedule instances
and a team registry, including source/dependency hashing, cache integrity and
shared semantic rules. Setup compiles/describes once; cold compilation is not
part of the measured operation. Missing/invalid results or a nonpositive,
nonfinite or >= 2-second mean fail the command. October 15, 2026 Windows
measurement: mean 906.736 ms across ten iterations (standard BDN outlier
filtering), 5.78 MB allocated per call. CI runs this gate on Windows and Linux;
remote results remain unrun until push. The pinned engine baseline compiles
only its copied engine harness with `ENGINE_BASELINE`, excluding newer
validator/group-commit dependencies unavailable in the older code revision.

```powershell
dotnet run -c Release --project backend\tests\Wf.Benchmarks -- --filter '*GroupCommit*'
```

The R-2 spike measures actual PostgreSQL event appends with `fsync`,
`full_page_writes` and synchronous commit enabled. Per-event, immediate and
requested 2 ms batching compare one/32 actors; each configuration also collects
1,024 individual acknowledgements and verifies snapshot-tail recovery.
`GroupCommitAppender` backpressures its bounded queue, commits contiguous event
groups atomically, acknowledges only after COMMIT and drains on normal disposal.
Cancellation before persistence skips a request; cancellation after persistence
starts cannot turn a committed write into a cancelled acknowledgement.

October 15 local measurements: 32-actor immediate-batch ack p95 46.723 ms on
Windows / 36.628 ms on Linux; requested 2 ms groups reduce commits from 1024 to 32
and ack p95 to 42.298 / 27.715 ms. Both profiles miss the 4 ms persistence target.
TD section 13 records the full table, environment and decision: immediate
coalescing by default, optional bounded delay for bursts, never early
acknowledgement of critical effects. This event-only prototype does not replace
the dispatch/result transactions. Azure HA remains unmeasured; no performance
sign-off is implied. `group-commit-metrics.json` accompanies BDN results;
`--artifacts` relocates these outputs too.

```powershell
dotnet run -c Release --project backend\tests\Wf.Benchmarks -- --filter '*Describe*'
```

The precompiled 200-node description benchmark exports full JSON and fails when
p95 reaches 1 second or measurements are missing. Workflow construction and
compilation are outside the measured operation. A local Windows measurement on
October 15, 2026: mean 0.483 ms, p95 0.524 ms. Generated reports stay under
`BenchmarkDotNet.Artifacts` and are ignored by git; results depend on the machine.

```powershell
dotnet run -c Release --project backend\tests\Wf.Benchmarks -- --filter '*EngineCore*' --metrics BenchmarkDotNet.Artifacts\engine-metrics.json
dotnet run -c Release --project backend\tests\Wf.Benchmarks -- --compare BenchmarkDotNet.Artifacts\baseline-metrics.json BenchmarkDotNet.Artifacts\engine-metrics.json
```

Execution measures a 200-node no-op chain through the actual local/core engine.
The run p95 must stay <= 3 s; per-node Ready-to-terminal p95 <= 10 ms, measured
from 1,000 individual nodes after warmup, not inferred by dividing run time.
Local Windows measurements: run mean 58.79 ms, p95 69.982 ms; node p95 1.236 ms.

`benchmarks.yml` executes the pinned code revision in
`backend/tests/Wf.Benchmarks/baseline.json` and current code on the same
Windows/Linux runner with identical measurement code. Either p95 regressing by
more than 10% fails, as do missing/invalid results or incompatible environments.
Each run uploads baseline/current metrics and full BenchmarkDotNet reports.
Baseline revision changes are explicit, reviewable code changes; there is no
first-run skip or automatic acceptance of slower results. Virtualized runners
can still be noisy; investigate a failed comparison rather than relaxing limits.

```powershell
dotnet run -c Release --project backend\tests\Wf.Benchmarks -- --run-query-bench
```

The run-list/filter gate seeds exactly 100,000 historical run summaries across
four teams, 20 types, 200 workflows and 200 series, spanning approximately 28
hours. Signed JWT requests use production authentication and fresh Viewer
authorization; only one team's 25,000 runs are visible. Each of ten scenarios
requires 100 measured requests after ten warmups with nearest-rank p95 strictly
below 300 ms. Scenarios cover unscoped authorized listing, team, type, status,
AND metadata, time range, combined filters, workflow, series and keyset paging
after 5,000 rows. Complete HTTP bodies, typed deserialization and exact identities,
ordering, values and page boundaries are verified against an independent seed
oracle inside each measured request.

Local Windows/.NET 10/PostgreSQL 17 results on October 16, 2026: every scenario
passes; worst p95 126.3951 ms (unscoped listing), team 33.693 ms, combined filters
21.2728 ms and deep paging 38.0153 ms. These are sequential warm loopback Kestrel
measurements, not cold-cache, concurrent-load, UI-rendering or remote-network
SLAs. No forced query plans, cache purges or production query/index changes.
The database contains summary rows rather than complete historical event graphs.
Reports include dataset, environment, durability settings and per-scenario
percentiles under ignored `BenchmarkDotNet.Artifacts\run-query\<id>\report.json`.
`WF_TEST_POSTGRES` uses a unique schema; otherwise an isolated PostgreSQL 17
container is created. Both modes require `fsync`, `full_page_writes` and
`synchronous_commit` ON and remove their own schema/container afterward.
`benchmarks.yml` runs the gate and its actual performance fixture on Windows
and Linux; ordinary backend tests exclude the `Performance` trait. Remote CI
execution is not implied by the local result.
