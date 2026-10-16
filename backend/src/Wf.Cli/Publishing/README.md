# Local/dev publication

```powershell
$env:WF_ACCESS_TOKEN = '<valid signed token with the Registrar application role>'
wf publish .\teams\payments\release\settle --engine http://localhost:4175 `
  --git-commit <lowercase-40-or-64-character-commit> --git-author 'Developer Name' `
  --change-note 'Test the release locally'
```

The engine uses its normal JWT validation and Registrar authorization. Publish requires
an explicit bearer token even on loopback: no unsigned tokens, login flow or auth bypass.
HTTPS origins only, except HTTP loopback; redirects are not followed. Credentials, paths,
queries and fragments in the engine URL are rejected. Tokens are never sent to upload
capability URLs or written to output.

Only trusted local `workflow.cs` is compiled. Publication uses `Validator` and
`PackageBuilder`, exactly as `wf validate --package` does, retaining deterministic ZIP
bytes for identical published inputs. No remote source retrieval or prebuilt-package
submission; URLs and UNC shares are rejected as inputs/work paths. Declare the
complete external dependency closure using repeated `--input`; use
`--work-directory` to isolate build/cache artifacts. `--baseline` and
`--team-type-quota` have the same meaning as validation.

Git commit and author are required, explicit provenance; the CLI never invents them.
The manifest must name `team/type/name`, match `metadata.type`, and match the owning
`team.json` when present. The nearest `team.json` within two ancestor levels is used,
matching validation; otherwise the team/type must already exist on the engine.
An existing `schedules.json` requires explicit `--schedule-source-path` with its exact
repository-relative path. Omitting a schedule file preserves the engine's existing
configuration; it does not delete schedules.

Source, registry, schedule and baseline inputs are limited to 1 MiB each, strict UTF-8
for text. Symlink/reparse-point input and work paths are rejected. Engine URI: 2048
characters; token: 16 KiB ASCII without whitespace; options: 256 arguments / 64 KiB,
4096 characters each. Server provenance, 8 MiB registration JSON and 512 MiB compiled
package limits apply. Changes detected during validation abort publication.

Upload grant → verified package upload (unless already present) → registration. No
automatic retries. A cancelled/failed registration request may already be durable:
inspect the engine before retrying. Exit codes: `0` registered or unchanged, `1`
validation or HTTP rejection, `2` usage/infrastructure/protocol failure, `130`
cancellation. Success is printed only after a valid engine registration receipt.
