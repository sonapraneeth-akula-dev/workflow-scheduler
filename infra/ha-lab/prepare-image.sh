#!/bin/sh
set -eu

ubuntu_build="$1"
k3s_version="$2"
image=ubuntu-24.04-server-cloudimg-amd64.img
source="https://cloud-images.ubuntu.com/releases/noble/release-${ubuntu_build}"
mkdir -p downloads
cd downloads

fetch() {
    if [ ! -f "$2" ]; then
        curl --fail --location --retry 3 --silent --show-error "$1" -o "$2.partial"
        mv "$2.partial" "$2"
    fi
}

fetch "$source/SHA256SUMS" SHA256SUMS
fetch "$source/SHA256SUMS.gpg" SHA256SUMS.gpg
gpgv --keyring /usr/local/share/ubuntu-cloudimage.gpg SHA256SUMS.gpg SHA256SUMS
fetch "$source/$image" "$image"
grep " [*]${image}$" SHA256SUMS | sha256sum --check --strict
if [ ! -f ../ubuntu-base.vhdx ]; then
    qemu-img convert -f qcow2 -O vhdx -o subformat=dynamic "$image" ../ubuntu-base.vhdx.partial
    mv ../ubuntu-base.vhdx.partial ../ubuntu-base.vhdx
fi

mkdir -p "$k3s_version"
cd "$k3s_version"
release="https://github.com/k3s-io/k3s/releases/download/$k3s_version"
fetch "$release/sha256sum-amd64.txt" sha256sum-amd64.txt
fetch "$release/k3s" k3s
grep '  k3s$' sha256sum-amd64.txt | sha256sum --check --strict
fetch "https://raw.githubusercontent.com/k3s-io/k3s/$k3s_version/install.sh" install.sh
