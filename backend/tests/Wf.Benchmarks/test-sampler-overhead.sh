#!/usr/bin/env bash
set -euo pipefail
[[ "${WF_SAMPLER_OVERHEAD_ISOLATED_CONTAINER:-}" == 1 && -f /.dockerenv && $EUID == 0 ]] ||
  { echo 'Requires an opted-in disposable container with private cgroup namespace and SYS_ADMIN/BPF/NET_ADMIN.' >&2; exit 2; }
repo="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$repo"
artifacts="$repo/.agent-artifacts/sampler-overhead-3adec91f/linux"
result_name="${WF_SAMPLER_OVERHEAD_RESULT_NAME:-measurements}"
[[ "$result_name" =~ ^[A-Za-z0-9_-]+$ ]] || { echo 'Invalid result directory name.' >&2; exit 2; }
mkdir -p "$artifacts"/{scratch,cli,packages,results,runtime,home,cgroup-mount,build}
export TMPDIR="$artifacts/scratch" DOTNET_CLI_HOME="$artifacts/cli" NUGET_PACKAGES="$artifacts/packages"
export XDG_RUNTIME_DIR="$artifacts/runtime" HOME="$artifacts/home" DOTNET_CLI_TELEMETRY_OPTOUT=1
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
chmod 0700 "$artifacts/runtime"
project=backend/tests/Wf.Benchmarks.Tests
properties=(--artifacts-path "$artifacts/build" -p:PublishAot=false -p:NuGetAudit=false
  -p:UseSharedCompilation=false '-p:WarningsNotAsErrors=NU1902%3BNU1903')
[[ -d /host-nuget ]] || { echo 'Mount existing NuGet cache read-only at /host-nuget; no external network required.' >&2; exit 2; }
if [[ "${WF_SAMPLER_OVERHEAD_PREBUILT:-}" != 1 ]]; then
  if [[ ! -f "$artifacts/build/obj/Wf.Benchmarks.Tests/project.assets.json" ]]; then
    dotnet restore "$project" "${properties[@]}" --source /host-nuget -p:RestoreFallbackFolders=/host-nuget -v:q
  fi
  dotnet test "$project" --no-restore -c Release "${properties[@]}" -p:RestoreFallbackFolders=/host-nuget \
    --disable-build-servers --filter FullyQualifiedName~SamplerOverhead \
    --logger 'trx;LogFileName=linux-sampler-overhead-units.trx' --results-directory "$artifacts/results" -v:q
  dotnet build-server shutdown
fi
mountpoint="$artifacts/cgroup-mount"
mount -t cgroup2 -o rw,nosuid,nodev,noexec none "$mountpoint"
cleanup() {
  local result=$?
  set +e
  printf '%s' "$$" > "$mountpoint/control/cgroup.procs"
  for ((attempt=0; attempt<100; attempt++)); do
    rmdir "$mountpoint/delegated/agent" 2>/dev/null && break
    sleep 0.05
  done
  rmdir "$mountpoint/delegated" || result=1
  printf '%s' '-cpu -memory' > "$mountpoint/cgroup.subtree_control" || result=1
  printf '%s' "$$" > "$mountpoint/cgroup.procs" || result=1
  rmdir "$mountpoint/control" || result=1
  umount "$mountpoint" || result=1
  printf 'Owned cgroups/mount cleaned; exit=%s\n' "$result"
  exit "$result"
}
trap cleanup EXIT
mkdir "$mountpoint/control"
printf '%s' "$$" > "$mountpoint/control/cgroup.procs"
printf '%s' '+cpu +memory' > "$mountpoint/cgroup.subtree_control"
mkdir "$mountpoint/delegated"
printf '%s' '+cpu +memory' > "$mountpoint/delegated/cgroup.subtree_control"
mkdir "$mountpoint/delegated/agent"
printf '%s' "$$" > "$mountpoint/delegated/agent/cgroup.procs"
chown -R 65534:65534 "$artifacts"
setpriv --reuid=65534 --regid=65534 --clear-groups \
  --bounding-set=-all,+bpf,+net_admin --inh-caps=+bpf,+net_admin --ambient-caps=+bpf,+net_admin --no-new-privs \
  dotnet "$artifacts/build/bin/Wf.Benchmarks/release/Wf.Benchmarks.dll" \
  --sampler-overhead-bench "$artifacts/$result_name" --cgroup-root "$mountpoint/delegated" "$@"
