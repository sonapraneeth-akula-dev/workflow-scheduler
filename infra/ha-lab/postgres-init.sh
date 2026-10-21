#!/bin/sh
set -eu
KARDIA_DATABASE_PASSWORD="$(cat /run/secrets/database_password)"
export KARDIA_DATABASE_PASSWORD
psql --username "$POSTGRES_USER" --dbname postgres --no-psqlrc --set ON_ERROR_STOP=1 <<'SQL'
\getenv app_password KARDIA_DATABASE_PASSWORD
SELECT format('CREATE ROLE kardia LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L', :'app_password') \gexec
CREATE DATABASE workflow OWNER kardia;
SQL
unset KARDIA_DATABASE_PASSWORD
