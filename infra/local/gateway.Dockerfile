ARG PREVIOUS_GATEWAY_IMAGE=caddy:2.11.7-alpine
FROM ${PREVIOUS_GATEWAY_IMAGE} AS previous

FROM oven/bun:1.4.3 AS frontend
ARG LOCAL_BUILD_MODE=local-compose
WORKDIR /src
COPY frontend/package.json frontend/bun.lock ./
RUN bun install --frozen-lockfile
COPY frontend/ ./
RUN test "$LOCAL_BUILD_MODE" = local-compose || test "$LOCAL_BUILD_MODE" = ha-lab
RUN bun run typecheck && VITE_AUTH_MODE="$LOCAL_BUILD_MODE" bunx --no-install vite build --mode "$LOCAL_BUILD_MODE"

FROM caddy:2.11.7-alpine
COPY infra/local/Caddyfile.gateway /etc/caddy/Caddyfile
COPY --from=previous /srv /opt/kardia/previous
COPY --from=frontend /src/dist /srv
USER 1000:1000
EXPOSE 8080
