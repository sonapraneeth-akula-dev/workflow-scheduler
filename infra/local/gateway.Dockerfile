FROM oven/bun:1.4.2 AS frontend
WORKDIR /src
COPY frontend/package.json frontend/bun.lock ./
RUN bun install --frozen-lockfile
COPY frontend/ ./
RUN bun run typecheck && VITE_AUTH_MODE=local-compose bunx --no-install vite build --mode local-compose

FROM caddy:2.11.6-alpine
COPY infra/local/Caddyfile.gateway /etc/caddy/Caddyfile
COPY --from=frontend /src/dist /srv
USER 1000:1000
EXPOSE 8080
