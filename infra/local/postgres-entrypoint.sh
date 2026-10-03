#!/bin/sh
set -eu
# Bind-mounted Windows key permissions cannot satisfy PostgreSQL's 0600 check.
mkdir -p /var/lib/postgresql/tls
cp /run/lab/postgres.crt /var/lib/postgresql/tls/server.crt
cp /run/lab/postgres.key /var/lib/postgresql/tls/server.key
chown -R postgres:postgres /var/lib/postgresql/tls
chmod 600 /var/lib/postgresql/tls/server.key
exec /usr/local/bin/docker-entrypoint.sh postgres -c ssl=on -c ssl_cert_file=/var/lib/postgresql/tls/server.crt -c ssl_key_file=/var/lib/postgresql/tls/server.key -c hba_file=/lab/pg_hba.conf -c log_statement=none -c log_min_error_statement=panic
