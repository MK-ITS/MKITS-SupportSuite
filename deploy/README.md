# Deployment assets

| Path | Content | Status |
|---|---|---|
| `docker/Dockerfile` | Server image (multi-stage, non-root, data volume `/data`) | available |
| `docker/docker-compose.yml` | Server + optional nginx | planned (Phase 3.5) |
| `nginx/` | Sample reverse-proxy configuration (TLS, WebSocket upgrade) | planned (Phase 3.5) |

The server listens on plain HTTP port 8080 inside the container. TLS is terminated by the reverse proxy,
which forwards `X-Forwarded-For` / `X-Forwarded-Proto`. See [ADR-0005](../docs/adr/0005-transport.md)
and [ADR-0010](../docs/adr/0010-server-deployment.md).
