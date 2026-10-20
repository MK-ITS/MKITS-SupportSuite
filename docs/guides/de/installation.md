# Installationsanleitung

> 🇬🇧 English version: [../en/installation.md](../en/installation.md)

Diese Anleitung installiert den **Server** der MKITS-SupportSuite auf Ubuntu Server mit Docker, hinter nginx mit einem Let's-Encrypt-Zertifikat.
Die Agents auf den Endgeräten werden später installiert (siehe [Administrationshandbuch](administration.md), Abschnitt 5).

> **Projektstand:** Das Produkt befindet sich in früher Entwicklung (0.1.0-dev). Die folgenden Schritte funktionieren bereits und liefern einen laufenden Server mit Health- und Versions-Endpunkt. Anmeldung, Sitzungen und Agents folgen in den nächsten Entwicklungsphasen – Abschnitte mit geplantem Verhalten sind mit *(geplant)* markiert.

## 1. Voraussetzungen

| Punkt | Minimum | Empfohlen |
|---|---|---|
| Betriebssystem | Ubuntu Server 26.04 LTS (64 Bit) | ebenso |
| CPU / RAM | 2 vCPU / 2 GB | 4 vCPU / 4 GB |
| Festplatte | 20 GB | 50 GB + Platz für Aufzeichnungen |
| Netzwerk | öffentliche IPv4 (und/oder IPv6), TCP **80** und **443** erreichbar | 100 Mbit/s symmetrisch (≈ 1–5 Mbit/s je aktiver Sitzung) |
| DNS | ein Hostname, der auf den Server zeigt, z. B. `support.example.com` | |

Es werden nur Port 80 (Zertifikatsprüfung und Umleitung) und 443 benötigt. Agents und Supporter verbinden sich ausschließlich per HTTPS/WSS über 443.

## 2. Server vorbereiten

```bash
sudo apt update && sudo apt full-upgrade -y
sudo timedatectl set-timezone Europe/Berlin      # optional, nur für Logs

# Firewall: SSH, HTTP, HTTPS
sudo apt install -y ufw
sudo ufw allow OpenSSH
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw enable
```

## 3. Docker installieren

Die Ubuntu-eigenen Pakete genügen:

```bash
sudo apt install -y docker.io docker-compose-v2
sudo systemctl enable --now docker
docker compose version      # muss eine Version ausgeben
```

(Alternativ das offizielle Docker-Repository verwenden – siehe docs.docker.com.)

## 4. Server-Container bereitstellen

```bash
sudo mkdir -p /opt/mkits && cd /opt/mkits
sudo curl -fsSLO https://raw.githubusercontent.com/MK-ITS/MKITS-SupportSuite/main/deploy/docker/docker-compose.yml
sudo curl -fsSL -o .env https://raw.githubusercontent.com/MK-ITS/MKITS-SupportSuite/main/deploy/docker/.env.example
sudo chmod 600 .env
sudo nano .env
```

Mindestens setzen:

```ini
MKITS_PUBLIC_BASE_URL=https://support.example.com
```

Starten:

```bash
sudo docker compose pull
sudo docker compose up -d
sudo docker compose ps                     # STATUS muss "running" sein
curl -fsS http://127.0.0.1:8080/health     # → Healthy
```

Der Container:

- lauscht nur auf `127.0.0.1:8080` (ohne nginx von außen nicht erreichbar),
- läuft als Nicht-Root-Benutzer mit schreibgeschütztem Dateisystem und ohne Linux-Capabilities,
- speichert alle dauerhaften Daten im Docker-Volume `mkits_mkits-data` (eingehängt unter `/data`).

> **Aus dem Quellcode bauen** statt das Image zu laden: Repository klonen, in `deploy/docker/docker-compose.yml` die Zeile `image:` durch den auskommentierten `build:`-Abschnitt ersetzen und `docker compose up -d --build` ausführen.

## 5. nginx und TLS-Zertifikat einrichten

```bash
sudo apt install -y nginx certbot

# 1) Zertifikat über die nginx-Standardseite beziehen (liefert /var/www/html auf Port 80 aus)
sudo certbot certonly --webroot -w /var/www/html -d support.example.com \
     --agree-tos -m admin@example.com --no-eff-email \
     --deploy-hook "systemctl reload nginx"

# 2) MKITS-Konfiguration installieren
sudo curl -fsSL -o /etc/nginx/sites-available/mkits.conf \
     https://raw.githubusercontent.com/MK-ITS/MKITS-SupportSuite/main/deploy/nginx/mkits.conf
sudo sed -i 's/support.example.com/IHR.HOST.NAME/g' /etc/nginx/sites-available/mkits.conf
sudo ln -s /etc/nginx/sites-available/mkits.conf /etc/nginx/sites-enabled/mkits.conf
sudo rm /etc/nginx/sites-enabled/default

# 3) Prüfen und aktivieren
sudo nginx -t && sudo systemctl reload nginx
```

Zertifikate werden automatisch erneuert (`certbot.timer`); nginx wird über den Deploy-Hook neu geladen.

Was die nginx-Konfiguration leistet:

- leitet HTTP auf HTTPS um, nur TLS 1.2/1.3, HSTS,
- leitet WebSockets (`/ws/`) mit langen Timeouts und ohne Pufferung weiter,
- setzt `X-Forwarded-For` auf die echte Client-Adresse (vom Client mitgeschickte Werte werden verworfen),
- begrenzt die Anfragerate für Anmeldung und Sitzungscodes (der Server sperrt auffällige IPs zusätzlich).

## 6. Prüfen

```bash
curl -fsS https://support.example.com/health
curl -fsS https://support.example.com/api/system/version
```

Erwartet: `Healthy` und ein JSON-Dokument mit Produkt, Version und Lizenz.

## 7. Erster Administrator

Beim ersten Start ohne Administrator schreibt der Server ein einmaliges **Setup-Token** ins Log:

```bash
sudo docker compose -f /opt/mkits/docker-compose.yml logs server | grep -i "setup token"
```

`https://support.example.com/setup` öffnen, Token eingeben und das erste Admin-Konto anlegen (Zwei-Faktor-Anmeldung ist für Admins Pflicht). Danach ist das Token ungültig.

## 8. Konfiguration

Der Server wird über Umgebungsvariablen in `/opt/mkits/.env` bzw. `docker-compose.yml` konfiguriert (Format `Abschnitt__Schlüssel`, Listeneinträge `Schlüssel__0`, `Schlüssel__1`, …). Nach Änderungen: `sudo docker compose up -d`.
Ungültige Werte stoppen den Server beim Start mit einer klaren Meldung in `docker compose logs server`.

| Variable | Standard | Beschreibung |
|---|---|---|
| `Mkits__PublicBaseUrl` | – (in Docker Pflicht) | Öffentliche URL, z. B. `https://support.example.com` |
| `Mkits__DataDirectory` | `/data` | Datenverzeichnis im Container |
| `Mkits__ReverseProxy__KnownProxies__0…n` | – | zusätzliche vertrauenswürdige Proxy-IPs |
| `Mkits__ReverseProxy__KnownNetworks__0…n` | `172.30.80.0/24` (Compose) | vertrauenswürdige Proxy-Netze (CIDR) |
| `Mkits__ReverseProxy__ForwardLimit` | `1` | Anzahl Proxy-Stufen |

Weitere Einstellungen (IP-Sperren, Sitzungen, SMTP, Aufbewahrung) stehen im [Administrationshandbuch](administration.md#10-einstellungsreferenz).

> ⚠️ Nur die eigenen, echten Proxys als vertrauenswürdig eintragen. Sonst könnten Clients ihre IP-Adresse fälschen und IP-Sperren umgehen.

### 8.1 Support-Clients für das Kundenportal

Das Portal bietet den Adhoc-Client zum Download an, sobald die Datei im Datenvolume liegt. Bis offizielle Releases im Image enthalten sind (ab Phase 8), kopieren Sie die Dateien selbst dorthin:

```bash
# Ordner einmalig anlegen
docker compose exec server mkdir -p /data/agents/windows-x64 /data/agents/linux-x64 /data/agents/linux-arm64 /data/agents/android
# Windows-Client (portable EXE)
docker compose cp MKITS-Support.exe server:/data/agents/windows-x64/MKITS-Support.exe
# Linux-Clients (AppImage) und Android (APK), falls vorhanden
docker compose cp MKITS-Support-x86_64.AppImage server:/data/agents/linux-x64/MKITS-Support.AppImage
docker compose cp MKITS-Support-aarch64.AppImage server:/data/agents/linux-arm64/MKITS-Support.AppImage
docker compose cp MKITS-Support.apk server:/data/agents/android/MKITS-Support.apk
```

*Administration → Einstellungen → Support-Clients* zeigt, welche Dateien gefunden wurden. Kunden laden die Datei unter einem Namen herunter, der Ihre Serveradresse und den Sitzungscode enthält (`MKITS-Support_support.example.com_12345678.exe`), sodass der Client ohne weitere Eingabe verbindet. Die Dateien werden bei jedem Download neu gelesen – ein Austausch erfordert keinen Neustart.

## 9. Updates

```bash
cd /opt/mkits
# vorher sichern (Abschnitt 10)
sudo docker compose pull
sudo docker compose up -d
sudo docker image prune -f
```

Datenbank-Migrationen laufen beim Start automatisch *(ab Phase 4)*. Sobald es Releases gibt, in `.env` eine feste Version eintragen (`MKITS_IMAGE=…:0.2.0`), um Updates kontrolliert einzuspielen; `edge` folgt immer dem neuesten Entwicklungsstand.

## 10. Sicherung und Wiederherstellung

Der gesamte Zustand liegt im Volume `mkits_mkits-data` (Datenbank, Schlüssel, Aufzeichnungen, Agent-Pakete). **Die Schlüssel werden zwingend benötigt** – ohne sie gehen Anmeldungen, Gerätevertrauen und Aufzeichnungen verloren.

Sicherung (kurze Unterbrechung, konsistent):

```bash
cd /opt/mkits
sudo docker compose stop server
sudo docker run --rm -v mkits_mkits-data:/data:ro -v /var/backups:/backup alpine \
     tar czf /backup/mkits-data-$(date +%F).tar.gz -C /data .
sudo docker compose start server
```

Wiederherstellung:

```bash
cd /opt/mkits
sudo docker compose down
sudo docker volume rm mkits_mkits-data
sudo docker volume create mkits_mkits-data
sudo docker run --rm -v mkits_mkits-data:/data -v /var/backups:/backup alpine \
     sh -c "tar xzf /backup/mkits-data-JJJJ-MM-TT.tar.gz -C /data && chown -R 1654:1654 /data"
sudo docker compose up -d
```

(`1654` ist der Nicht-Root-Benutzer `app` der offiziellen .NET-Images.) Sicherungen verschlüsselt und außer Haus aufbewahren – sie enthalten Schlüsselmaterial.

## 11. Deinstallation

```bash
cd /opt/mkits
sudo docker compose down --volumes      # --volumes löscht ALLE Daten unwiderruflich
sudo rm -rf /opt/mkits /etc/nginx/sites-enabled/mkits.conf /etc/nginx/sites-available/mkits.conf
sudo systemctl reload nginx
```

## 12. Fehlerbehebung

| Symptom | Ursache / Lösung |
|---|---|
| `502 Bad Gateway` | Container läuft nicht: `sudo docker compose ps`, `sudo docker compose logs server` |
| Container startet ständig neu | Konfigurationsfehler – das Log nennt die ungültige Einstellung (`OptionsValidationException`) |
| `/health` meldet `Unhealthy` | Datenvolume nicht beschreibbar – Volume und Besitzer (`1654`) prüfen |
| Agents/Viewer verbinden sich nicht (WebSocket-Fehler) | `/ws/`-Block in nginx fehlt oder wurde verändert (Upgrade-/Connection-Header) |
| Alle Anfragen scheinen von `172.30.80.1` zu kommen | Proxy-Vertrauen fehlt – `Mkits__ReverseProxy__KnownNetworks__0` aus der Compose-Datei beibehalten |
| Zertifikatsfehler | `sudo certbot certificates`, `sudo certbot renew --dry-run` |
