# Workflow CLI

Run `wf --help` for validation, publishing, aborting and cloning.

## Clone an immutable version

```powershell
wf clone payments/release/deploy `
  --version 019ed132-2f44-7000-8000-000000000001 `
  --new-name experiment --target-type payments/release `
  --repo C:\workflows --engine http://127.0.0.1:5001
```

`--version` requires an exact lowercase canonical, nonempty UUID, never an
active/latest alias or semantic version. `--engine` overrides `WF_ENGINE_URL`;
HTTPS required except on loopback. `WF_ACCESS_TOKEN` supplies bearer
authentication, required outside loopback. Existing Development loopback
no-login behavior remains available. The API freshly checks Viewer access to
the source and Author access to the target type.

`--repo` defaults to the current directory and must already exist. The command
atomically creates `teams/<target-team>/<target-type>/<new-name>` containing
`workflow.cs`, `schedules.json`, `schemas/schedules.schema.json`,
`WorkflowClone.Tests.csproj`, `Tests/CloneTests.cs`, and `README.md`. Existing
files/directories are never overwritten. Linked paths, nonportable target
names, unexpected/duplicate/archive-link entries and oversized or failed
downloads are rejected; staging is removed on failure/cancellation.

Source is downloaded, **never executed** by cloning. The scaffold preserves
the selected historical source, changes identity/type, resets version to
`1.0.0`, and records `ClonedFrom = source@UUID`. Schedules start empty; no
workflow is registered, activated or deployed. Review source and target
metadata requirements before explicitly running the generated validation/tests
or publishing. Registry configuration is not copied.

Exit codes: `0` downloaded and published locally; `1` API rejection (including
400/403/404/409); `2` usage, download, unsafe archive or filesystem failure;
`130` cancellation before publication. Requests are not retried or redirected.
API rejection output includes HTTP status and recognized problem `code` (or
authorization `reason`); untrusted server details and bearer tokens are not
printed.
