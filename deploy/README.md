# Deployment assets

| Path | Content | Status |
|---|---|---|
| `docker/Dockerfile` | Server image (multi-stage, non-root, data volume `/data`) | available |
| `docker/docker-compose.yml`, `docker/.env.example` | Hardened server deployment (read-only, no capabilities, localhost only) | available |
| `nginx/mkits.conf` | Host nginx reverse proxy (TLS, WebSocket upgrade, rate limits) | available |

The server listens on plain HTTP port 8080 inside the container. TLS is terminated by the reverse proxy,
which forwards `X-Forwarded-For` / `X-Forwarded-Proto`. See [ADR-0005](../docs/adr/0005-transport.md)
and [ADR-0010](../docs/adr/0010-server-deployment.md).

Step-by-step: [installation guide (EN)](../docs/guides/en/installation.md) · [Installationsanleitung (DE)](../docs/guides/de/installation.md).
