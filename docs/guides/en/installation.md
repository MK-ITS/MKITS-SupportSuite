# Installation guide

> 🇩🇪 German version: [../de/installation.md](../de/installation.md)

This guide installs the MKITS-SupportSuite **server** on Ubuntu Server with Docker, behind nginx with a Let's Encrypt certificate.
Agents are installed on end devices later (see the [administration guide](administration.md), section 5).

> **Project status:** the product is in early development (0.1.0-dev). The installation steps below already work and give you a running server with health and version endpoints. Login, sessions and agents follow in the next development phases – sections that describe planned behavior are marked *(planned)*.

## 1. Requirements

| Item | Minimum | Recommended |
|---|---|---|
| OS | Ubuntu Server 26.04 LTS (64-bit) | same |
| CPU / RAM | 2 vCPU / 2 GB | 4 vCPU / 4 GB |
| Disk | 20 GB | 50 GB + space for recordings |
| Network | public IPv4 (and/or IPv6), TCP **80** and **443** reachable | 100 Mbit/s symmetric (≈ 1–5 Mbit/s per active session) |
| DNS | a host name pointing to the server, e.g. `support.example.com` | |

Only ports 80 (certificate validation and redirect) and 443 are needed. All agents and supporters connect via HTTPS/WSS on 443.

## 2. Prepare the server

```bash
sudo apt update && sudo apt full-upgrade -y
sudo timedatectl set-timezone Europe/Berlin      # optional, logs only

# Firewall: SSH, HTTP, HTTPS
sudo apt install -y ufw
sudo ufw allow OpenSSH
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw enable
```

## 3. Install Docker

Ubuntu's own packages are sufficient:

```bash
sudo apt install -y docker.io docker-compose-v2
sudo systemctl enable --now docker
docker compose version      # must print a version
```

(Alternatively use Docker's official repository – see docs.docker.com.)

## 4. Deploy the server container

```bash
sudo mkdir -p /opt/mkits && cd /opt/mkits
sudo curl -fsSLO https://raw.githubusercontent.com/MK-ITS/MKITS-SupportSuite/main/deploy/docker/docker-compose.yml
sudo curl -fsSL -o .env https://raw.githubusercontent.com/MK-ITS/MKITS-SupportSuite/main/deploy/docker/.env.example
sudo chmod 600 .env
sudo nano .env
```

Set at least:

```ini
MKITS_PUBLIC_BASE_URL=https://support.example.com
```

Start it:

```bash
sudo docker compose pull
sudo docker compose up -d
sudo docker compose ps                     # STATUS must be "running"
curl -fsS http://127.0.0.1:8080/health     # → Healthy
```

The container:

- listens only on `127.0.0.1:8080` (not reachable from outside without nginx),
- runs as a non-root user with a read-only file system and no Linux capabilities,
- stores all persistent data in the Docker volume `mkits_mkits-data` (mounted at `/data`).

> **Building from source** instead of pulling the image: clone the repository, then in `deploy/docker/docker-compose.yml` replace `image:` by the commented `build:` section and run `docker compose up -d --build`.

## 5. Install nginx and the TLS certificate

```bash
sudo apt install -y nginx certbot

# 1) Obtain the certificate using the default nginx site (serves /var/www/html on port 80)
sudo certbot certonly --webroot -w /var/www/html -d support.example.com \
     --agree-tos -m admin@example.com --no-eff-email \
     --deploy-hook "systemctl reload nginx"

# 2) Install the MKITS site configuration
sudo curl -fsSL -o /etc/nginx/sites-available/mkits.conf \
     https://raw.githubusercontent.com/MK-ITS/MKITS-SupportSuite/main/deploy/nginx/mkits.conf
sudo sed -i 's/support.example.com/YOUR.HOST.NAME/g' /etc/nginx/sites-available/mkits.conf
sudo ln -s /etc/nginx/sites-available/mkits.conf /etc/nginx/sites-enabled/mkits.conf
sudo rm /etc/nginx/sites-enabled/default

# 3) Test and activate
sudo nginx -t && sudo systemctl reload nginx
```

Certificates renew automatically (`certbot.timer`); nginx is reloaded by the deploy hook.

What the nginx configuration does:

- redirects HTTP to HTTPS, TLS 1.2/1.3 only, HSTS,
- forwards WebSockets (`/ws/`) with long timeouts and without buffering,
- sets `X-Forwarded-For` to the real client address (client-supplied values are discarded),
- rate-limits login and session-code endpoints (the server additionally bans abusive IPs).

## 6. Verify

```bash
curl -fsS https://support.example.com/health
curl -fsS https://support.example.com/api/system/version
```

Expected: `Healthy` and a JSON document with product, version and license.

## 7. First administrator

On first start without any administrator, the server writes a one-time **setup token** to its log:

```bash
sudo docker compose -f /opt/mkits/docker-compose.yml logs server | grep -i "setup token"
```

Open `https://support.example.com/setup`, enter the token and create the first Admin account (two-factor authentication is mandatory for Admins). The token becomes invalid afterwards.

## 8. Configuration

The server is configured with environment variables in `/opt/mkits/.env` / `docker-compose.yml` (format `Section__Key`, array items `Key__0`, `Key__1`, …). After changes: `sudo docker compose up -d`.
Invalid values stop the server at startup with a clear message in `docker compose logs server`.

| Variable | Default | Description |
|---|---|---|
| `Mkits__PublicBaseUrl` | – (required in Docker) | Public URL, e.g. `https://support.example.com` |
| `Mkits__DataDirectory` | `/data` | Data directory inside the container |
| `Mkits__ReverseProxy__KnownProxies__0…n` | – | Additional trusted proxy IPs |
| `Mkits__ReverseProxy__KnownNetworks__0…n` | `172.30.80.0/24` (compose) | Trusted proxy networks (CIDR) |
| `Mkits__ReverseProxy__ForwardLimit` | `1` | Number of proxy hops |

Further settings (IP bans, sessions, SMTP, retention) are listed in the [administration guide](administration.md#10-settings-reference).

> ⚠️ Only list your real proxies as trusted. If an untrusted address is listed, clients could fake their IP address and bypass IP bans.

### 8.1 Support clients for the customer portal

**The Windows client and the Linux client (AppImage, x86_64) are included in the server image** (same version as the server): the customer portal offers them for ad-hoc sessions, and Admins download the Windows client as `MKITS-Agent.exe` and the Linux agent package `mkits-agent_amd64.deb` for permanent devices (*Device administration* → create an enrollment token → links next to the token). Nothing needs to be copied.

Files in the data volume take precedence over the bundled ones – e.g. a code-signed build (ADR-0009) or clients for further platforms:

```bash
# Create the folders once
docker compose exec server mkdir -p /data/agents/windows-x64 /data/agents/linux-x64 /data/agents/linux-arm64 /data/agents/android
# Optional: your own (e.g. signed) Windows build instead of the bundled one
docker compose cp MKITS-Support.exe server:/data/agents/windows-x64/MKITS-Support.exe
# Linux clients (AppImage) and Android (APK), once available
docker compose cp MKITS-Support-x86_64.AppImage server:/data/agents/linux-x64/MKITS-Support.AppImage
docker compose cp MKITS-Support-aarch64.AppImage server:/data/agents/linux-arm64/MKITS-Support.AppImage
docker compose cp MKITS-Support.apk server:/data/agents/android/MKITS-Support.apk
```

Remove a copied file again to return to the bundled version (otherwise an old copy would stay in use after server updates). *Administration → Settings → Support clients* shows which files were found. Customers download the file under a name that contains your server address and the session code (`MKITS-Support_support.example.com_12345678.exe`), so the client connects without further input. Files are read on every download – replacing them needs no restart.

> The bundled client is not code-signed yet: Windows SmartScreen may warn on first start (*More info → Run anyway*).

## 9. Updates

```bash
cd /opt/mkits
# make a backup first (section 10)
sudo docker compose pull
sudo docker compose up -d
sudo docker image prune -f
```

Database migrations run automatically at startup *(from Phase 4 on)*. Pin a version tag in `.env` (`MKITS_IMAGE=…:0.2.0`) for controlled upgrades once releases exist; `edge` always follows the latest development build.

## 10. Backup and restore

All state is in the volume `mkits_mkits-data` (database, keys, recordings, agent packages). **The keys are required** – without them existing logins, device trust and recordings are lost.

Backup (short downtime, consistent):

```bash
cd /opt/mkits
sudo docker compose stop server
sudo docker run --rm -v mkits_mkits-data:/data:ro -v /var/backups:/backup alpine \
     tar czf /backup/mkits-data-$(date +%F).tar.gz -C /data .
sudo docker compose start server
```

Restore:

```bash
cd /opt/mkits
sudo docker compose down
sudo docker volume rm mkits_mkits-data
sudo docker volume create mkits_mkits-data
sudo docker run --rm -v mkits_mkits-data:/data -v /var/backups:/backup alpine \
     sh -c "tar xzf /backup/mkits-data-YYYY-MM-DD.tar.gz -C /data && chown -R 1654:1654 /data"
sudo docker compose up -d
```

(`1654` is the non-root `app` user of the official .NET images.) Store backups encrypted and off-site – they contain key material.

## 11. Uninstall

```bash
cd /opt/mkits
sudo docker compose down --volumes      # --volumes deletes ALL data irrevocably
sudo rm -rf /opt/mkits /etc/nginx/sites-enabled/mkits.conf /etc/nginx/sites-available/mkits.conf
sudo systemctl reload nginx
```

## 12. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `502 Bad Gateway` | container not running: `sudo docker compose ps`, `sudo docker compose logs server` |
| Container restarts in a loop | configuration error – the log names the invalid setting (`OptionsValidationException`) |
| `/health` returns `Unhealthy` | data volume not writable – check volume and ownership (`1654`) |
| Agents/viewers cannot connect (WebSocket errors) | nginx `/ws/` block missing or modified (Upgrade/Connection headers) |
| All requests appear to come from `172.30.80.1` | trusted proxy setting missing – keep `Mkits__ReverseProxy__KnownNetworks__0` from the compose file |
| Certificate errors | `sudo certbot certificates`, `sudo certbot renew --dry-run` |
