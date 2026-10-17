#!/usr/bin/env bash
set -euo pipefail

executable=''
agent_id=''
agent_name="$(< /proc/sys/kernel/hostname)"
validate_only=false
native_resource_sampling=false
engines=()
while (($#)); do
  case "$1" in
    --executable|--engine|--id|--name)
      if (($# < 2)); then echo "Missing value for $1" >&2; exit 2; fi
      case "$1" in
        --executable) executable="$2" ;;
        --engine) engines+=("$2") ;;
        --id) agent_id="$2" ;;
        --name) agent_name="$2" ;;
      esac
      shift 2 ;;
    --validate-only) validate_only=true; shift ;;
    --native-resource-sampling) native_resource_sampling=true; shift ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done
[[ -f "$executable" && -x "$executable" ]] || { echo '--executable must name a published executable' >&2; exit 2; }
((${#engines[@]})) || { echo 'At least one --engine is required' >&2; exit 2; }
[[ "$agent_name" =~ ^[a-zA-Z0-9._-]{1,200}$ ]] || { echo 'Invalid agent name' >&2; exit 2; }
if [[ -n "$agent_id" ]]; then
  [[ "$agent_id" =~ ^[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}$ &&
      "$agent_id" != 00000000-0000-0000-0000-000000000000 ]] || { echo 'Invalid agent UUID' >&2; exit 2; }
fi
origin_pattern='^https?://(\[[0-9a-fA-F:]+\]|[a-zA-Z0-9._-]+)(:[0-9]{1,5})?/?$'
for engine in "${engines[@]}"; do
  [[ "$engine" =~ $origin_pattern ]] || { echo 'Engine must be an HTTP(S) origin' >&2; exit 2; }
  if [[ "$engine" =~ :([0-9]+)/?$ ]]; then
    port="${BASH_REMATCH[1]}"
    ((10#$port >= 1 && 10#$port <= 65535)) || { echo 'Invalid engine port' >&2; exit 2; }
  fi
  if [[ "$engine" == http://* &&
        ! "$engine" =~ ^http://(localhost|127\.[0-9]+\.[0-9]+\.[0-9]+|\[::1\])(:[0-9]+)?/?$ ]]; then
    echo 'Non-loopback engines require HTTPS' >&2; exit 2
  fi
done
unit="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/wf-agent.service"
[[ -f "$unit" ]] || { echo "Unit not found: $unit" >&2; exit 2; }
resource_profile="$(dirname -- "$unit")/wf-agent-resources.conf"
if $native_resource_sampling; then
  [[ -f "$resource_profile" ]] || { echo "Resource profile not found: $resource_profile" >&2; exit 2; }
fi
if $validate_only; then echo 'Agent installer inputs valid; no changes made.'; exit 0; fi
((EUID == 0)) || { echo 'Run this installer as root' >&2; exit 1; }
[[ ! -e /etc/systemd/system/wf-agent.service && ! -e /etc/kardia/wf-agent.env && ! -e /opt/kardia/wf-agent/wf-agent ]] ||
  { echo 'Agent installation already exists; refusing to overwrite it' >&2; exit 1; }
systemd_version="$(systemctl --version)"
systemd_version="${systemd_version#systemd }"
systemd_version="${systemd_version%% *}"
[[ "$systemd_version" =~ ^[0-9]+$ ]] && ((systemd_version >= 254)) ||
  { echo 'Agent resource limits require systemd >=254 (DelegateSubgroup).' >&2; exit 2; }
if $native_resource_sampling; then
  [[ -x /usr/bin/setpriv ]] ||
    { echo 'Native resource sampling requires /usr/bin/setpriv (util-linux) to drop task capabilities.' >&2; exit 2; }
  [[ ! -e /etc/systemd/system/wf-agent.service.d/resources.conf ]] ||
    { echo 'Resource service profile already exists; refusing to overwrite it' >&2; exit 1; }
fi
[[ -n "$agent_id" ]] || agent_id="$(cat /proc/sys/kernel/random/uuid)"
if ! id -u wf-agent >/dev/null 2>&1; then useradd --system --home-dir /var/lib/wf-agent --shell /usr/sbin/nologin wf-agent; fi
install -d -m 0755 /opt/kardia/wf-agent /etc/kardia
install -m 0755 "$executable" /opt/kardia/wf-agent/wf-agent
umask 077
{
  printf 'Agent__Id=%s\nAgent__Name=%s\n' "$agent_id" "$agent_name"
  for index in "${!engines[@]}"; do printf 'Agent__Engines__%s=%s\n' "$index" "${engines[$index]}"; done
} > /etc/kardia/wf-agent.env
install -m 0644 "$unit" /etc/systemd/system/wf-agent.service
if $native_resource_sampling; then
  install -d -m 0755 /etc/systemd/system/wf-agent.service.d
  install -m 0644 "$resource_profile" /etc/systemd/system/wf-agent.service.d/resources.conf
fi
systemctl daemon-reload
systemctl enable --now wf-agent.service
systemctl is-active --quiet wf-agent.service
echo "Installed wf-agent ($agent_id) as the wf-agent system user."
