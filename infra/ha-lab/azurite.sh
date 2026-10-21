#!/bin/sh
set -eu
AZURITE_ACCOUNTS="kardialab:$(cat /run/secrets/blob_key)"
export AZURITE_ACCOUNTS
exec azurite-blob --blobHost 0.0.0.0 --blobPort 10000 --location /data \
    --disableProductStyleUrl --skipApiVersionCheck --disableTelemetry --silent
