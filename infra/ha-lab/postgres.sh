#!/bin/sh
set -eu
mkdir -p /run/kardia-postgres
cp /certs/server.crt /run/kardia-postgres/server.crt
cp /certs/server.key /run/kardia-postgres/server.key
chown postgres:postgres /run/kardia-postgres/server.*
chmod 600 /run/kardia-postgres/server.key
exec /usr/local/bin/docker-entrypoint.sh postgres \
    -c ssl=on \
    -c ssl_min_protocol_version=TLSv1.2 \
    -c ssl_cert_file=/run/kardia-postgres/server.crt \
    -c ssl_key_file=/run/kardia-postgres/server.key \
    -c hba_file=/scripts/pg_hba.conf
