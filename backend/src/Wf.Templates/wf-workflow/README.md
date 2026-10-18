# WorkflowSample

Requires the .NET 10 SDK and a NuGet source containing matching `Wf.Sdk` and
`Wf.Sdk.Testing` packages (default `1.0.0`; select with `--sdkVersion`).

Run `dotnet test` in this directory. Tests publish the real `workflow.cs` once,
then execute its dry-run host in separate processes; task delegates never run.
Only `Tests/*.cs` compile into the test project. Do not add a project reference
to `workflow.cs`: C# file-based apps cannot be referenced (CS9298).
The test project selects xUnit v3's built-in Microsoft Testing Platform through
its MSBuild integration; no VSTest adapter/test-host launch is needed.
Keep this integration when using the current harness cache: .NET 10's native
`global.json` MTP mode injects a different `TESTINGPLATFORM_DOTNETTEST_EXECUTIONID`
on each invocation, invalidating the harness's all-environment content hash.
The template does not remove environment variables or weaken cache integrity.

The fixture checks the workflow, schedules, published schedule schema and exact
API baseline, then demonstrates success, output assertions, retries and skipped
dependencies. `MockSpec` has no implicit default; mock every task explicitly.
One assembly fixture builds the workflow once. Independent test classes may run
in parallel; each dry-run starts a separate process with its own mock file.

`workflow.cs` uses NuGet, not repository-relative project references. Its
versionless `#:package Wf.Sdk` directive takes the pinned version from
`Directory.Packages.props`; an inline version conflicts with central management.
`PublishAot=false` keeps IL required by the task host. To describe the real
workflow without executing tasks, run `dotnet run --file workflow.cs -- --describe`.

`schedules.json` demonstrates categorised defaults, stable instance IDs, cron
or interval (never both), IANA timezone IDs, overlap/catch-up policies and
instance variable overrides. Its editor schema lives in `schemas/`.
Update `workflow.api.json` deliberately when changing public variables/nodes
and follow SemVer; validation must not silently rewrite this baseline.

First `dotnet test` includes restore, test compilation and workflow publishing.
Subsequent runs may reuse `bin/workflow-cache`; the harness checks source,
configuration, SDK and environment before reusing artifacts. Cold runs are not
guaranteed to finish within 10 seconds. The integration budget measures the
complete warm `dotnet test` invocation with populated NuGet/build/workflow
caches; restore, build checks, cache validation and all four tests still run.
CI must cache packages and measure cold publication separately.
No repository CI gate is installed by this template.
