#!/usr/bin/env bash
set -euo pipefail

[[ "${WF_RESOURCE_ISOLATED_CONTAINER:-}" == 1 && -f /.dockerenv && $EUID == 0 ]] ||
  { echo 'Run only in an explicitly opted-in disposable root container with BPF, NET_ADMIN, SYS_ADMIN.' >&2; exit 2; }
repo="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo"
artifacts="${WF_RESOURCE_ARTIFACTS:-$repo/.agent-artifacts/wf-resource-sampler-3adec91f-linux}"
[[ "$artifacts" == "$repo/.agent-artifacts/"* ]] || { echo 'Artifacts must stay inside this project .agent-artifacts directory.' >&2; exit 2; }
mkdir -p "$artifacts"/{cgroup-mount,scratch,cli,packages,results,test-files,runtime,home}
chmod 0700 "$artifacts/runtime"
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
  if [[ -d "$mountpoint/delegated/agent" ]]; then
    echo 'Owned service cgroup still has processes:'
    cat "$mountpoint/delegated/agent/cgroup.procs"
    result=1
  fi
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
# .NET 10 named mutexes require a real per-user runtime directory for identities without a home.
export XDG_RUNTIME_DIR="$artifacts/runtime" HOME="$artifacts/home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 MSBUILDDISABLENODEREUSE=1
export WF_RESOURCE_TEST_CGROUP_ROOT="$mountpoint/delegated" WF_RESOURCE_TEST_ARTIFACTS="$artifacts/test-files"
if [[ "${1:-}" == --without-bpf ]]; then
  export WF_RESOURCE_EXPECT_UNAVAILABLE=1
  export WF_RESOURCE_TEST_FILTER=Native_resource_rights_state_does_not_stop_actual_task
fi
mode="${1:-root}"
mode="${mode#--}"
arguments=(test backend/tests/Wf.Agent.Tests --filter "${WF_RESOURCE_TEST_FILTER:-ResourceSampler}"
  --disable-build-servers --artifacts-path "$artifacts" -p:PublishAot=false -p:UseSharedCompilation=false
  '-p:WarningsNotAsErrors=NU1902%3BNU1903'
  --logger "trx;LogFileName=linux-native-resource-sampler-$mode.trx" --results-directory "$artifacts/results")
[[ "${WF_RESOURCE_OFFLINE_RESTORE:-}" != 1 ]] || arguments+=(-p:NuGetAudit=false)
[[ ! -d /host-nuget ]] || arguments+=(-p:RestoreFallbackFolders=/host-nuget)
if [[ "${1:-}" == --nonroot || "${1:-}" == --without-bpf ]]; then
  # Model a delegated service identity: no SYS_ADMIN, no privilege acquisition, only BPF + NET_ADMIN.
  chown -R 65534:65534 "$artifacts"
  export WF_RESOURCE_TEST_IDENTITY="${1#--}"
  capabilities=(--bounding-set=-all,+bpf,+net_admin --inh-caps=+bpf,+net_admin --ambient-caps=+bpf,+net_admin)
  [[ "${1:-}" != --without-bpf ]] || capabilities=(--bounding-set=-all)
  setpriv --reuid=65534 --regid=65534 --clear-groups "${capabilities[@]}" --no-new-privs \
    dotnet "${arguments[@]}"
else
  dotnet "${arguments[@]}"
fi
