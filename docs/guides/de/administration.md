# Administrationshandbuch

> 🇬🇧 English version: [../en/administration.md](../en/administration.md)

Für Betreiber und Admins eines MKITS-SupportSuite-Servers. Installation: [installation.md](installation.md).

> **Projektstand (0.1.0-dev):** Dieses Handbuch beschreibt das Zielverhalten, damit Betreiber vorausplanen können. Die Statusspalte in Abschnitt 1 zeigt, was heute schon verfügbar ist.

## 1. Funktionsstand

| Bereich | Status |
|---|---|
| Server-Container, nginx-Einrichtung, Health-/Versions-Endpunkt, Konfigurationsprüfung | ✅ verfügbar |
| Sicherheits-Header, vertrauenswürdige Reverse Proxys, Datenverzeichnis, Lokalisierung (DE/EN) | ✅ verfügbar |
| Ersteinrichtung, Anmeldung, Benutzer, Rollen, Zwei-Faktor (TOTP), Profil/Sprache | ✅ verfügbar |
| IP-Sperren, Allow-/Deny-Listen (Konfiguration), Audit-Log (Aufzeichnung) | ✅ verfügbar |
| Admin-Oberfläche für IP-Sperren und Audit-Log, E-Mail-Benachrichtigungen | 🔜 Phase 4 (als Nächstes) |
| Adhoc-Sitzungen, Kundenportal, Zustimmung, Relay | 🔜 Phasen 5–6 |
| Windows-Agent, Browser-Viewer (Chat, Dateien, Zwischenablage, Shell) | 🔜 Phasen 7–9 (Meilenstein M1) |
| Gerätegruppen, dauerhafter Zugriff, Installer, Auto-Update | 🔜 Phase 10 (M2) |
| Linux-Agent (X11 + Wayland) | 🔜 Phase 11 (M3) |
| Android-Agent | 🔜 Phase 12 (M4) |
| Desktop-Viewer, Sitzungsaufzeichnung | 🔜 Phase 13 (M5) |

## 2. Grundbegriffe

**Rollen**

| Rolle | Darf |
|---|---|
| Guest (nicht angemeldet) | Kundenportal öffnen, Sitzungscode eingeben, Client herunterladen und Hilfe erhalten |
| User | alles wie Guest, zusätzlich anmelden, Adhoc-Sitzungen anlegen und sich als Supporter auf Geräte der freigegebenen Gruppen aufschalten |
| Admin | alles wie User, zusätzlich Benutzer, Gerätegruppen, dauerhafte Geräte, Richtlinien, IP-Sperren, Einstellungen, Audit-Log und Aufzeichnungen verwalten |

**Sitzungsarten**

- **Adhoc:** Ein Supporter legt eine Sitzung an und nennt dem Kunden einen 8-stelligen Code. Der Kunde gibt ihn im Portal ein, startet den heruntergeladenen Client und stimmt der Verbindung zu. Es bleibt nichts installiert.
- **Dauerhaft:** Auf einem Gerät ist ein Agent installiert und einer **Gerätegruppe** zugeordnet. Berechtigte Supporter können sich jederzeit gemäß der **Zustimmungsrichtlinie** der Gruppe aufschalten.

**Zustimmungsrichtlinien** (je Gerätegruppe): *Fragen* (Benutzer am Gerät muss zustimmen), *Hinweis* (nur Banner), *Still* (keine Abfrage – nur wo rechtlich zulässig, z. B. unbeaufsichtigte Server). Adhoc-Sitzungen verwenden immer *Fragen*. Ein sichtbares Banner und die Schaltfläche *Sitzung beenden* sind am Endgerät immer vorhanden.

**Sicherheitsmodell in einem Satz:** Der gesamte Verkehr läuft über HTTPS/WSS 443 durch Ihren Server, die Sitzungsinhalte sind zwischen Agent und Viewer Ende-zu-Ende-verschlüsselt, sodass der Server sie nicht lesen kann – außer bei Sitzungen, die Sie ausdrücklich aufzeichnen (allen Beteiligten angekündigt).

## 3. Erste Anmeldung und Zwei-Faktor-Anmeldung

1. Ersten Admin mit dem Setup-Token anlegen (Installationsanleitung, Abschnitt 7).
2. QR-Code mit einer Authenticator-App scannen (z. B. Microsoft Authenticator, Aegis, 2FAS) und die **Wiederherstellungscodes** sicher aufbewahren.
3. Oberflächensprache (Deutsch/Englisch) im Profil wählen. Ohne Profileinstellung gilt die Browsersprache.

## 4. Benutzer

*Administration → Benutzer*

- **Benutzer anlegen:** Benutzername, E-Mail, Anzeigename (wird Kunden im Zustimmungsdialog angezeigt), Rolle, Sprache. Der Server erzeugt ein Startpasswort, das **einmalig** angezeigt wird – sicher weitergeben; der Benutzer ändert es unter *Mein Konto*.
- **Passwort zurücksetzen:** erzeugt ein neues Passwort (einmalig angezeigt) und entsperrt das Konto.
- **2FA zurücksetzen:** entfernt den Authenticator; Admins müssen ihn bei der nächsten Anmeldung neu einrichten. Alle bestehenden Anmeldungen des Benutzers werden innerhalb einer Minute beendet.
- **Zum Admin / zum User machen:** ändert die Rolle.
- Das eigene Konto kann nicht deaktiviert oder herabgestuft werden, und der letzte aktive Admin ist geschützt.
- **Benutzer deaktivieren:** sperrt die Anmeldung sofort und beendet seine aktiven Sitzungen. Die Audit-Historie bleibt erhalten.
- **2FA-Richtlinie:** Pflicht für Admins (Admin-Seiten sind ohne sie nicht erreichbar); User können sie unter *Mein Konto* aktivieren. Eine Pflicht auch für User ist geplant (`Mkits__Security__RequireTwoFactorForUsers`).
- **Passwörter:** mindestens 12 Zeichen, keine Zeichenklassen-Vorgaben (lange Passphrasen sind besser). Nach 10 Fehlversuchen wird ein Konto für 15 Minuten gesperrt (zusätzlich zu IP-Sperren).

## 5. Geräte und dauerhafter Zugriff *(geplant)*

### 5.1 Gerätegruppen

*Administration → Gerätegruppen* – typischerweise eine Gruppe je Kunde oder Standort.

| Einstellung | Bedeutung |
|---|---|
| Zustimmungsrichtlinie | Fragen / Hinweis / Still |
| Aufzeichnungsrichtlinie | Aus / Optional (Supporter entscheidet) / Pflicht |
| Freigabe neuer Geräte erforderlich | neue Geräte bleiben *ausstehend*, bis ein Admin sie freigibt |
| Berechtigungen | je User: Ansehen, Steuern, Dateien, Zwischenablage, Shell |

### 5.2 Geräte registrieren

In der Gruppe ein **Registrierungs-Token** erzeugen (Gültigkeit, maximale Anzahl Verwendungen). Dann:

- **Windows:** MSI auf der Gruppenseite herunterladen und installieren, z. B. per GPO/Intune:
  `msiexec /i MKITS-Agent.msi /qn MKITS_SERVER=https://support.example.com MKITS_TOKEN=<token>`
- **Linux (Debian/Ubuntu):** `sudo apt install ./mkits-agent_<version>_amd64.deb && sudo mkits-agent enroll --server https://support.example.com --token <token>`
- **Android:** APK aus dem Portal installieren, App öffnen, Registrierungs-QR-Code scannen, auf Nachfrage die Bedienungshilfe aktivieren.

Tokens werden nur einmal angezeigt, nur als Hash gespeichert und können widerrufen werden. Nach einem Rollout Tokens widerrufen.

### 5.3 Geräte verwalten

Die Geräteliste zeigt Online-Status, Betriebssystem, Agent-Version, zuletzt gesehen und letzte IP. Aktionen: umbenennen, in andere Gruppe verschieben, deaktivieren (blockiert Verbindungen), entfernen (Agent muss neu registriert werden). Agents aktualisieren sich automatisch vom Server (signierte Updates).

## 6. Ablauf Adhoc-Support *(geplant)*

1. *Sitzungen → Neue Adhoc-Sitzung*, optional eine Referenz (Ticketnummer) eingeben. Der Code ist 10 Minuten gültig.
2. Den Kunden bitten, `https://support.example.com` zu öffnen und den Code einzugeben.
3. Der Kunde lädt den Client herunter und startet ihn; dieser zeigt Ihre Serveradresse und Ihren Anzeigenamen und fragt nach der Zustimmung.
4. Sitzung im Browser öffnen. In sensiblen Fällen den 6-stelligen **Sicherheitscode** am Telefon mit dem Kunden vergleichen – er beweist, dass niemand (auch kein kompromittierter Server) dazwischensitzt.
5. Werkzeuge: Monitorauswahl, Strg+Alt+Entf, Chat, Dateiübertragung, Zwischenablage, Remote-Shell, *Mit Administratorrechten neu starten* (Kunde bestätigt die UAC-Abfrage).
6. Sitzung beenden. Der Client entfernt sich selbst.

## 7. Sicherheitsverwaltung

### 7.1 IP-Sperren *(geplant)*

Fehlgeschlagene Anmeldungen, falsche 2FA-Codes, falsche Sitzungscodes, ungültige Registrierungs-Tokens und ungültige Gerätesignaturen werden je IP-Adresse gezählt (IPv6 je /64-Netz). Standard: **5 Fehlversuche innerhalb von 10 Minuten → 7 Tage gesperrt.**

*Administration → Sicherheit → IP-Sperren*: aktive Sperren ansehen, Sperre aufheben, manuelle Sperre anlegen, exportieren.

- **Allow-Liste:** Netze, die nie gesperrt werden (z. B. Ihr Büro). Keine Netze eintragen, die Sie nicht kontrollieren.
- **Deny-Liste:** Netze, die immer abgewiesen werden.

Jede Sperre und Entsperrung wird im Audit-Log festgehalten; Admins können per E-Mail benachrichtigt werden (Abschnitt 8).

### 7.2 Vertrauen in den Reverse Proxy

Der Server übernimmt Client-IP-Adressen nur von den in `Mkits__ReverseProxy__*` eingetragenen Proxys (Installationsanleitung, Abschnitt 8). Nach der Installation prüfen, dass das Audit-Log echte Client-IPs zeigt und nicht `172.30.80.1`.

### 7.3 Empfehlungen

- Anzahl der Admins klein halten; nur personenbezogene Konten verwenden.
- Zustimmungsrichtlinie *Still* nur für unbeaufsichtigte Server und nur wo rechtlich zulässig (Benutzer informieren, Betriebsrat).
- Audit-Log regelmäßig prüfen und die Integritätsprüfung ausführen.
- Host aktuell halten (`unattended-upgrades`) und MKITS zeitnah aktualisieren.
- Datenvolume täglich sichern; Sicherungen schützen (sie enthalten Schlüssel).

## 8. E-Mail-Benachrichtigungen *(geplant)*

*Administration → Einstellungen → E-Mail* oder per Umgebungsvariablen:

```ini
Mkits__Smtp__Host=smtp.example.com
Mkits__Smtp__Port=587
Mkits__Smtp__Security=StartTls        # StartTls | SslOnConnect | None
Mkits__Smtp__UserName=mkits@example.com
Mkits__Smtp__Password=…               # besser: Docker-Secret / .env mit chmod 600
Mkits__Smtp__From=MKITS Support <mkits@example.com>
```

Benachrichtigungen: IP-Sperren, neue Geräte warten auf Freigabe, fehlgeschlagene Integritätsprüfung, Server-Update verfügbar.

## 9. Audit-Log und Aufzeichnungen *(geplant)*

- *Administration → Audit-Log*: Filter nach Zeit, Benutzer, Gerät, Ereignistyp, IP. Export als CSV/JSON. Das Log ist per Hash-Kette verkettet; *Integrität prüfen* erkennt veränderte oder gelöschte Einträge.
- *Administration → Aufzeichnungen*: abspielen, herunterladen oder löschen. Das Abspielen wird protokolliert. Aufzeichnungen werden nach Ablauf der Aufbewahrungsfrist automatisch gelöscht.

## 10. Einstellungsreferenz

Umgebungsvariablen (`Abschnitt__Schlüssel`) haben Vorrang vor Einstellungen aus der Admin-Oberfläche. ✅ = heute verfügbar, 🔜 = geplant (Name kann sich bis zur Freigabe der Funktion noch ändern).

| Einstellung | Standard | | Beschreibung |
|---|---|---|---|
| `Mkits__PublicBaseUrl` | – | ✅ | öffentliche URL des Servers |
| `Mkits__DataDirectory` | `/data` | ✅ | Datenverzeichnis |
| `Mkits__ReverseProxy__KnownProxies__N` | – | ✅ | vertrauenswürdige Proxy-IPs |
| `Mkits__ReverseProxy__KnownNetworks__N` | – | ✅ | vertrauenswürdige Proxy-Netze (CIDR) |
| `Mkits__ReverseProxy__ForwardLimit` | `1` | ✅ | Proxy-Stufen |
| `Mkits__IpBan__MaxFailures` | `5` | 🔜 | Fehlversuche bis zur Sperre |
| `Mkits__IpBan__Window` | `00:10:00` | 🔜 | Zählzeitraum |
| `Mkits__IpBan__BanDuration` | `7.00:00:00` | 🔜 | Sperrdauer (t.hh:mm:ss) |
| `Mkits__IpBan__IPv6PrefixLength` | `64` | 🔜 | IPv6-Zusammenfassung |
| `Mkits__IpBan__AllowList__N` / `DenyList__N` | – | 🔜 | CIDR-Listen (zusätzlich zur Admin-Oberfläche) |
| `Mkits__Sessions__CodeLength` | `8` | 🔜 | Stellen des Adhoc-Codes (6–12) |
| `Mkits__Sessions__CodeLifetime` | `00:10:00` | 🔜 | Gültigkeit unbenutzter Codes |
| `Mkits__Sessions__ConsentTimeout` | `00:02:00` | 🔜 | Zeit für die Zustimmung am Gerät |
| `Mkits__Sessions__SuspendGracePeriod` | `00:01:00` | 🔜 | Wiederverbindungsfenster nach Verbindungsabbruch |
| `Mkits__Sessions__MaxDuration` | `08:00:00` | 🔜 | maximale Sitzungsdauer |
| `Mkits__Security__RequireTwoFactorForUsers` | `false` | 🔜 | 2FA auch für User |
| `Mkits__Smtp__*` | – | 🔜 | E-Mail (Abschnitt 8) |
| `Mkits__Retention__AuditLogDays` | `365` | 🔜 | Aufbewahrung Audit-Log |
| `Mkits__Retention__RecordingDays` | `30` | 🔜 | Aufbewahrung Aufzeichnungen |
| `Mkits__Retention__FailedAttemptDays` | `30` | 🔜 | Aufbewahrung Fehlversuche |

## 11. Gespeicherte Daten (Datenschutz)

| Daten | Zweck | Aufbewahrung |
|---|---|---|
| Benutzerkonten (Name, E-Mail, Passwort-Hash, 2FA-Geheimnis) | Anmeldung | bis zur Löschung |
| Geräte (Name, Betriebssystem, Agent-Version, letzte IP, zuletzt gesehen) | dauerhafter Zugriff | bis zur Entfernung |
| Sitzungsmetadaten (wer, wann, welches Gerät, Dauer, IPs, Zustimmungsergebnis) | Betrieb, Nachvollziehbarkeit | Audit-Aufbewahrung |
| Audit-Log | Nachvollziehbarkeit | 365 Tage (einstellbar) |
| Fehlversuche und IP-Sperren | Schutz vor Brute-Force-Angriffen | 30 Tage / Sperrdauer |
| Aufzeichnungen (nur wenn aktiviert und allen Beteiligten angekündigt) | Dokumentation | 30 Tage (einstellbar) |

Sitzungsinhalte (Bildschirm, Tastatureingaben, Dateien, Chat, Shell) nicht aufgezeichneter Sitzungen sind Ende-zu-Ende-verschlüsselt und werden auf dem Server **nicht** gespeichert. Sie sind Verantwortlicher im Sinne der DSGVO; informieren Sie Ihre Benutzer und Kunden entsprechend.

## 12. Überwachung und Logs

- Health: `https://support.example.com/health` (`Healthy` / `Unhealthy`) – geeignet für Uptime-Monitoring.
- Version: `https://support.example.com/api/system/version`.
- Logs: `sudo docker compose -f /opt/mkits/docker-compose.yml logs -f server` (von Docker rotiert: 5 × 20 MB).
- Logs enthalten niemals Passwörter, Sitzungscodes, Tokens oder Schlüssel.
