FROM ubuntu:24.04@sha256:68ee22004a9109b44973313b02ff7b1de250f1de0f5ecb628d73f579aaff33ac
RUN apt-get update \
    && apt-get install --no-install-recommends -y ca-certificates curl gnupg qemu-utils genisoimage \
    && rm -rf /var/lib/apt/lists/* \
    && curl --fail --silent --show-error --location \
       'https://keyserver.ubuntu.com/pks/lookup?op=get&search=0xD2EB44626FDDC30B513D5BB71A5D6C4C7DB87C81' \
       | gpg --dearmor > /usr/local/share/ubuntu-cloudimage.gpg
WORKDIR /work
