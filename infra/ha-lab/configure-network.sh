#!/bin/sh
set -eu
umask 077

address="$1"
uplink_mac="$2"
private_mac="$3"
case "$address" in
    192.168.247.11|192.168.247.12|192.168.247.13) ;;
    *) echo 'Invalid lab-private address.' >&2; exit 1 ;;
esac
for mac in "$uplink_mac" "$private_mac"; do
    echo "$mac" | grep -Eq '^([0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}$' || {
        echo 'Invalid lab adapter MAC address.' >&2
        exit 1
    }
done

target=/etc/netplan/90-kardia-lab.yaml
cat > "$target.next" <<EOF
network:
  version: 2
  ethernets:
    eth0:
      match:
        macaddress: $uplink_mac
      set-name: eth0
      dhcp4: true
    kardia0:
      match:
        macaddress: $private_mac
      set-name: kardia0
      addresses: [$address/24]
      optional: true
EOF
if [ -f "$target" ] && cmp --silent "$target.next" "$target"; then
    rm "$target.next"
    ip -4 address show dev kardia0 | grep -Fq "$address/24"
    touch /run/kardia-lab-network-ready
else
    mv "$target.next" "$target"
    netplan generate
    rm -f /run/kardia-lab-network-ready
    # Applying netplan can renew DHCP and sever this SSH session.
    systemd-run --quiet --collect --unit=kardia-lab-network-apply --on-active=2s \
        --timer-property=RemainAfterElapse=no /bin/sh -ec \
        'netplan apply; touch /run/kardia-lab-network-ready'
fi
