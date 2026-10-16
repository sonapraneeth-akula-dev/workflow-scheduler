#!/usr/bin/env bash
set -euo pipefail
[[ "${WF_RESOURCE_ISOLATED_CONTAINER:-}" == 1 && -f /.dockerenv && $EUID == 0 ]] ||
  { echo 'Use an explicitly opted-in disposable container with BPF, NET_ADMIN, SYS_ADMIN.' >&2; exit 2; }
[[ -n "${WF_TEST_POSTGRES:-}" ]] || { echo 'An isolated PostgreSQL connection is required.' >&2; exit 2; }
repo="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../../.." && pwd)"
cd "$repo"
artifacts="$repo/.agent-artifacts/resource-shipping-3adec91f/linux"
mountpoint="$artifacts/cgroup-mount"
ipc="$repo/.agent-artifacts/resource-shipping-3adec91f/s"
mkdir -p "$artifacts"/{cgroup-mount,scratch,cli,packages,results,runtime,home} "$ipc"
chmod 0700 "$artifacts/runtime"
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
export TMPDIR="$artifacts/scratch" DOTNET_CLI_HOME="$artifacts/cli" NUGET_PACKAGES="$artifacts/packages"
export XDG_RUNTIME_DIR="$artifacts/runtime" HOME="$artifacts/home" DOTNET_CLI_TELEMETRY_OPTOUT=1
export WF_RESOURCE_TEST_CGROUP_ROOT="$mountpoint/delegated" WF_RESOURCE_TEST_ARTIFACTS="$artifacts"
export WF_RESOURCE_TEST_IPC_ROOT="$ipc" WF_RESOURCE_REQUIRE_NETWORK=1
mode="${1:-root}"
mode="${mode#--}"
[[ "$mode" != without-bpf ]] || export WF_RESOURCE_REQUIRE_NETWORK=0
arguments=(test backend/tests/Wf.Integration.Tests --no-restore --filter "${WF_RESOURCE_TEST_FILTER:-ResourceMetrics}" --disable-build-servers
  --artifacts-path "$artifacts" -p:PublishAot=false -p:UseSharedCompilation=false -p:NuGetAudit=false
  '-p:WarningsNotAsErrors=NU1902%3BNU1903' --logger "trx;LogFileName=${WF_RESOURCE_TEST_RESULT_NAME:-linux-resource-metrics-$mode.trx}"
  --results-directory "$artifacts/results" -v:q)
[[ ! -d /host-nuget ]] || arguments+=(-p:RestoreFallbackFolders=/host-nuget)
[[ "${WF_RESOURCE_TEST_NO_BUILD:-}" != 1 ]] || arguments+=(--no-build)
if [[ "$mode" == nonroot || "$mode" == without-bpf ]]; then
  chown -R 65534:65534 "$artifacts" "$ipc"
  capabilities=(--bounding-set=-all,+bpf,+net_admin --inh-caps=+bpf,+net_admin --ambient-caps=+bpf,+net_admin)
  [[ "$mode" != without-bpf ]] || capabilities=(--bounding-set=-all)
  setpriv --reuid=65534 --regid=65534 --clear-groups "${capabilities[@]}" --no-new-privs dotnet "${arguments[@]}"
else
  dotnet "${arguments[@]}"
fi
