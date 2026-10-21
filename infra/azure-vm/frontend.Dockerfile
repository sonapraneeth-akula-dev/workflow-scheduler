ARG PREVIOUS_GATEWAY_IMAGE=caddy:2.11.7-alpine
FROM ${PREVIOUS_GATEWAY_IMAGE} AS previous

FROM oven/bun:1.4.3 AS frontend
ARG VITE_ENTRA_TENANT_ID
ARG VITE_ENTRA_SPA_CLIENT_ID
ARG VITE_ENTRA_API_SCOPE
WORKDIR /src
COPY frontend/package.json frontend/bun.lock ./
RUN bun install --frozen-lockfile
COPY frontend/ ./
RUN test -n "$VITE_ENTRA_TENANT_ID" && test -n "$VITE_ENTRA_SPA_CLIENT_ID" && test -n "$VITE_ENTRA_API_SCOPE"
RUN bun run typecheck && VITE_AUTH_MODE=entra \
    VITE_ENTRA_TENANT_ID="$VITE_ENTRA_TENANT_ID" \
    VITE_ENTRA_SPA_CLIENT_ID="$VITE_ENTRA_SPA_CLIENT_ID" \
    VITE_ENTRA_API_SCOPE="$VITE_ENTRA_API_SCOPE" bunx --no-install vite build --mode production

FROM caddy:2.11.7-alpine
RUN setcap -r /usr/bin/caddy
COPY infra/azure-vm/Caddyfile /etc/caddy/Caddyfile
COPY --from=previous /srv /opt/kardia/previous
COPY --from=frontend /src/dist /srv
USER 1000:1000
EXPOSE 8443
