#!/usr/bin/env bash
set -euo pipefail

fail() { echo "$*" >&2; exit 1; }
[[ -f /sys/fs/cgroup/cgroup.controllers ]] || fail 'A unified cgroup-v2 hierarchy is required.'
script="$(readlink -f -- "${BASH_SOURCE[0]}")"

if [[ "${1:-}" == --boot ]]; then
  [[ "${WF_CGROUP_ISOLATED_CONTAINER:-}" == 1 && -f /.dockerenv && $EUID == 0 &&
      "$(cat /proc/self/cgroup)" == '0::/' ]] ||
    fail 'Boot only in an opted-in disposable container with a private cgroup namespace.'
  mount -o remount,rw /sys/fs/cgroup
  exec /sbin/init
fi

if [[ "${1:-}" == --probe ]]; then
  ((EUID != 0)) || fail 'Delegation must be exercised by a non-root identity.'
  [[ "$(id -un)" == wf-agent ]] || fail 'Unexpected service identity.'
  [[ "$(awk '/^CapEff:/ {print $2}' /proc/self/status)" == 0000000000000000 ]] ||
    fail 'The probe must have no effective capabilities.'
  relative="$(awk -F: '$1 == "0" {print $3}' /proc/self/cgroup)"
  [[ "$relative" == */agent ]] || fail "Service was not placed in DelegateSubgroup=agent: $relative"
  root="/sys/fs/cgroup${relative%/agent}"
  [[ ! -s "$root/cgroup.procs" || -z "$(cat "$root/cgroup.procs")" ]] ||
    fail 'Delegated parent contains processes.'
  printf '%s' '+cpu +memory' > "$root/cgroup.subtree_control"
  task="$root/wf-task-$$"
  mkdir -- "$task"
  cleanup_probe() { rmdir -- "$task"; }
  trap cleanup_probe EXIT
  printf '%s' '25000 100000' > "$task/cpu.max"
  printf '%s' 67108864 > "$task/memory.max"
  printf '%s' 0 > "$task/memory.swap.max"
  bash -c 'printf "%s" "$$" > "$1/cgroup.procs"; exec sleep 0.2' wf-task "$task" &
  child=$!
  found=false
  for ((attempt=0; attempt<100; attempt++)); do
    if grep -qx "$child" "$task/cgroup.procs"; then found=true; break; fi
    sleep 0.001
  done
  wait "$child"
  $found || fail 'The task never entered its owned cgroup.'
  [[ "$(cat "$task/cpu.max")" == '25000 100000' &&
      "$(cat "$task/memory.max")" == 67108864 &&
      "$(cat "$task/memory.swap.max")" == 0 ]] || fail 'Resource limits did not persist.'
  grep -q '^usage_usec ' "$task/cpu.stat"
  [[ -z "$(cat "$task/cgroup.procs")" ]] || fail 'Task cgroup did not empty after exit.'
  echo "PASS identity=$(id -un) uid=$EUID capabilities=none group=$relative cpu=0.25 memory=64MiB swap=0 child-joined=true cleanup=true"
  exit 0
fi

[[ "${1:-}" == --test && "${WF_CGROUP_ISOLATED_CONTAINER:-}" == 1 && -f /.dockerenv &&
    $EUID == 0 && "$(cat /proc/1/comm)" == systemd ]] ||
  fail 'Run --test only as root in an opted-in disposable systemd container.'
source_dir="$(dirname -- "$script")"
unit=wf-delegation-test.service
scope=wf-delegation-scope.scope
cleanup() {
  local result=$?
  trap - EXIT
  systemctl stop "$unit" || result=1
  if systemctl is-active --quiet "$scope"; then systemctl stop "$scope" || result=1; fi
  rm -f -- "/run/systemd/system/$unit"
  systemctl daemon-reload || result=1
  exit "$result"
}
trap cleanup EXIT
cp -- "$source_dir/wf-agent.service" "/run/systemd/system/$unit"
# Retain shipped hardening; replace only the executable/notification dependency.
cat >> "/run/systemd/system/$unit" <<EOF

[Service]
Type=oneshot
ExecStart=
ExecStart=$script --probe
EnvironmentFile=
Restart=no
RemainAfterExit=yes
EOF
sed '/^\(AmbientCapabilities\|CapabilityBoundingSet\|Environment\)=/d' \
  "$source_dir/wf-agent-resources.conf" >> "/run/systemd/system/$unit"
systemctl daemon-reload
cat /etc/os-release
/proc/1/exe --version | head -n 1
uname -r
systemctl start "$unit"
journalctl --no-pager -u "$unit" -o cat
[[ "$(systemctl show "$unit" -p Result --value)" == success ]] || fail 'Service probe failed.'
systemctl stop "$unit"

# A system scope is an administrator-provisioned fallback, not an unprivileged polkit bypass.
systemd-run --scope --quiet --unit="$scope" --property=Delegate=cpu\ memory \
  /usr/bin/bash -c '
    set -euo pipefail
    root="/sys/fs/cgroup$(awk -F: '"'"'$1 == "0" {print $3}'"'"' /proc/self/cgroup)"
    mkdir "$root/agent"
    printf "%s" "$$" > "$root/agent/cgroup.procs"
    owner="$(id -u wf-agent):$(id -g wf-agent)"
    chown "$owner" "$root" "$root/cgroup.procs" "$root/cgroup.threads" "$root/cgroup.subtree_control" "$root/agent"
    exec setpriv --reuid=wf-agent --regid=wf-agent --clear-groups --bounding-set=-all --no-new-privs "$1" --probe
  ' wf-scope "$script"
journalctl --no-pager -u "$scope" -o cat

[[ ! -e /etc/systemd/system/wf-agent.service && ! -e /opt/kardia/wf-agent/wf-agent ]] ||
  fail 'Installer smoke refuses an existing agent installation.'
printf '%s\n' '#!/usr/bin/env bash' \
  'exec /usr/bin/systemd-notify --ready --exec \; /usr/bin/sleep infinity' > "$source_dir/notify-agent"
chmod 0755 "$source_dir/notify-agent"
bash -n "$source_dir/install-agent.sh"
bash "$source_dir/install-agent.sh" --executable "$source_dir/notify-agent" \
  --engine https://engine.invalid --native-resource-sampling --validate-only
bash "$source_dir/install-agent.sh" --executable "$source_dir/notify-agent" \
  --engine https://engine.invalid --native-resource-sampling
systemctl is-active --quiet wf-agent.service
[[ "$(systemctl show wf-agent.service -p Delegate --value)" == yes &&
    "$(systemctl show wf-agent.service -p DelegateSubgroup --value)" == agent ]] ||
  fail 'Installed service did not retain delegation.'
systemctl stop wf-agent.service
echo 'PASS real systemd service delegation, administrator-provisioned scope fallback and installer smoke (notification stub, not the agent binary).'
