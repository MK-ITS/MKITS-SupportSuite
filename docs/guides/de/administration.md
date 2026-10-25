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
| IP-Sperren, Allow-/Deny-Listen, Audit-Log mit Integritätsprüfung, E-Mail-Benachrichtigungen (Admin-Oberfläche) | ✅ verfügbar |
| Adhoc-Sitzungen: Codes, Kundenportal, Windows-Client mit Zustimmung, Browser-Viewer (Bildschirm, Eingabe, Chat, Dateien, Zwischenablage, Shell), Administrator-Modus | ✅ verfügbar (Meilenstein M1) |
| Gerätegruppen, Berechtigungen, Registrierungstokens, Geräteliste, Supporter-Seite *Geräte* | ✅ verfügbar |
| Windows-Agent als Dienst (unbeaufsichtigter Zugriff, Zustimmungsrichtlinien, Sitzungsfenster mit Chat/Dateien/Zwischenablage/Shell) | ✅ verfügbar (siehe §5.2) |
| Agent-Auto-Update (signiert) | ✅ verfügbar, sobald der Release-Schlüssel eingerichtet ist |
| MSI-Paket | ✖ nicht geplant – die EXE installiert sich selbst (ADR-0024) |
| Linux-Adhoc-Client (AppImage, X11-Desktops): Bildschirm, Eingabe, Chat, Dateien, Zwischenablage, Terminal | ✅ verfügbar |
| Dauerhafter Linux-Agent (systemd-Dienst, .deb; Bildschirm/Eingabe auf X11-Desktops, root-Shell) | ✅ verfügbar (siehe §5.2) |
| Linux auf Wayland-Desktops, arm64 | 🔜 Phase 11 (M3) |
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

## 5. Geräte und dauerhafter Zugriff

### 5.1 Gerätegruppen

*Geräteverwaltung* (`/admin/devices`) – typischerweise eine Gruppe je Kunde oder Standort.

| Einstellung | Bedeutung |
|---|---|
| Zustimmung des Benutzers am Gerät | **Jedes Mal fragen** (Ja/Nein-Meldung am Gerät; niemand angemeldet → abgelehnt), **Nur benachrichtigen** (Meldung ohne Rückfrage), **Still** (nichts – nur wo rechtlich zulässig, z. B. unbeaufsichtigte Server) |
| Neue Geräte benötigen Freigabe | neue Geräte warten auf Freigabe, bis ein Admin sie freigibt |
| Berechtigungen | je User: Ansehen, Steuern, Dateien, Shell, Zwischenablage (jede Berechtigung schließt Ansehen ein; Admins haben immer alle). Server und Agent setzen sie pro Funktion durch. |

### 5.2 Geräte registrieren

In der Gruppe ein **Registrierungstoken** erzeugen (Gültigkeit, Anzahl Geräte). Es wird **einmalig** zusammen mit dem Installationsbefehl angezeigt. Dann:

- **Windows** (10 / Server 2016 oder neuer, x64): `MKITS-Agent.exe` herunterladen (Link beim Token) und in einer **Administrator**-Eingabeaufforderung oder per Softwareverteilung / RMM-Werkzeug ausführen:
  `MKITS-Agent.exe --install --server https://support.example.com --token <token>`
  Der Agent installiert sich nach `%ProgramFiles%\MKITS-SupportSuite\Agent`, richtet den Dienst **MKITS SupportSuite Agent** ein (automatischer Start, Neustart bei Fehlern) und registriert sich. Identität und Einstellungen liegen DPAPI-geschützt in `%ProgramData%\MKITS-SupportSuite\agent` (nur SYSTEM und Administratoren); dort liegt auch das Protokoll `agent.log`. Ein erneutes `--install` aktualisiert den Agent und behält die Identität (`--reenroll` erzwingt eine neue Registrierung, dann ist ein Token nötig). Entfernen mit `MKITS-Agent.exe --uninstall`. **Updates:** Installierte Agents aktualisieren sich selbst vom Server (Prüfung alle paar Stunden, nur außerhalb von Sitzungen), sobald Ihr Server ein neueres Image verwendet – vorausgesetzt, das Image ist mit dem Release-Schlüssel des Projekts signiert (ADR-0023); andernfalls `--install` mit der neuen EXE erneut ausführen.
  Bildschirm, Maus und Tastatur funktionieren auch am Anmeldebildschirm, auf dem Sperrbildschirm und bei UAC-Abfragen (inklusive Strg+Alt+Entf). Ist ein Benutzer angemeldet, erscheint für die Dauer einer Sitzung ein Sitzungsfenster (bei *Still* verborgen): Chat, Dateiübertragung, Zwischenablage und Shell laufen dort mit den Rechten **dieses Benutzers**. Ohne angemeldeten Benutzer stehen nur Bildschirm und Eingabe zur Verfügung.

#### Massenverteilung (Intune, GPO, RMM)

Es gibt kein MSI-Paket (ADR-0024); die EXE installiert sich selbst. Verwenden Sie ein Token mit kurzer Gültigkeit und begrenzter Anzahl Geräte – Verteilungskonfigurationen sind für andere Administratoren (Intune) bzw. alle Domänenbenutzer (SYSVOL) lesbar. Für breite Rollouts *Neue Geräte benötigen Freigabe* aktivieren. Das Token danach widerrufen.

- **Intune (Win32-App):** `MKITS-Agent.exe` mit dem *Microsoft Win32 Content Prep Tool* verpacken.
  Installationsbefehl: `MKITS-Agent.exe --install --server https://support.example.com --token <token>` (Installationsverhalten: *System*).
  Deinstallationsbefehl: `"%ProgramFiles%\MKITS-SupportSuite\Agent\MKITS-Agent.exe" --uninstall`.
  Erkennungsregel: Datei `%ProgramFiles%\MKITS-SupportSuite\Agent\MKITS-Agent.exe` vorhanden.
- **GPO (Computer-Startskript):** `MKITS-Agent.exe` auf eine für *Domänencomputer* lesbare Freigabe legen und ein Startskript hinzufügen:
  ```bat
  sc query MKITSAgent >nul 2>&1 || "\\fileserver\deploy$\MKITS-Agent.exe" --install --server https://support.example.com --token <token>
  ```
- **RMM / Softwareverteilung:** den Installationsbefehl als SYSTEM oder Administrator ausführen.

Exit-Codes: `0` OK, `2` ungültige Argumente, `5` nicht als Administrator, `6` Dienstfehler, `7` Dateifehler. Details stehen in `%ProgramData%\MKITS-SupportSuite\agent\agent.log`.

- **Linux** (Debian 12+, Ubuntu 22.04+, x64): `mkits-agent_amd64.deb` herunterladen (Link beim Token) und als root ausführen:
  ```bash
  sudo apt install ./mkits-agent_amd64.deb
  sudo mkits-agent --install --server https://support.example.com --token <token>
  ```
  Das Paket installiert den Agent nach `/opt/mkits-agent` und den systemd-Dienst **mkits-agent** (Start beim Booten, Neustart bei Fehlern); `--install` speichert Server und Token und registriert das Gerät. Konfiguration (`/etc/mkits-agent`) und Identität (`/var/lib/mkits-agent`) sind nur für root lesbar; das Token wird nach der Registrierung gelöscht. Log: `journalctl -u mkits-agent`. `--reenroll` erzwingt eine neue Registrierung, `sudo mkits-agent --uninstall` trennt das Gerät, `sudo apt purge mkits-agent` entfernt alles. **Updates:** wie unter Windows aktualisieren sich installierte Agents selbst vom Server (signiertes Paket, nur außerhalb von Sitzungen; Log: `journalctl -u 'mkits-agent-update-*'`) – vorausgesetzt, das Image ist mit dem Release-Schlüssel des Projekts signiert (ADR-0023). Andernfalls das neuere Paket manuell installieren (`sudo apt install ./mkits-agent_amd64.deb`); der Dienst startet damit neu.
  Bildschirm, Maus und Tastatur funktionieren, solange ein Benutzer an einem **X11**-Desktop angemeldet ist (folgt Ab-/Anmeldungen und Benutzerwechseln); Wayland-Desktops und der Anmeldebildschirm werden noch nicht unterstützt. Das Sitzungsfenster (Chat, Dateien, Zwischenablage; bei *Still* unsichtbar) läuft mit den Rechten des angemeldeten Benutzers. **Die Remote-Shell läuft als root** – auf Servern ohne Desktop ist sie das wichtigste Werkzeug. Vergeben Sie die Berechtigung *Shell* für Linux-Geräte nur an Personen, die diese administrieren dürfen (ADR-0025). Ohne grafische Sitzung wird *Jedes Mal fragen* abgelehnt (niemand zum Fragen da); für Server *Nur benachrichtigen* oder *Still* verwenden.
  Exit-Codes: `0` OK, `2` ungültige Argumente, `5` nicht root, `6` systemd-Fehler, `7` Dateifehler (oder Paket fehlt).
- **Android:** geplant (Phase 12).

Tokens werden nur als Hash gespeichert und können widerrufen werden. Nach einem Rollout Tokens widerrufen.

### 5.3 Geräte verwalten

Die Geräteliste zeigt Gruppe, Betriebssystem, Agent-Version, zuletzt gesehen und letzte IP. Aktionen: freigeben, in eine andere Gruppe verschieben, deaktivieren (beendet eine laufende Sitzung und trennt das Gerät), entfernen (Agent muss neu registriert werden). Supporter sehen die Geräte, auf die sie Zugriff haben, unter **Geräte** (`/devices`) mit dem Online-Zustand und verbinden sich dort oder treten einer laufenden Sitzung bei.

## 6. Ablauf Adhoc-Support *(teilweise verfügbar)*

Schritte 1–2 sind verfügbar (Menü *Sitzungen*, Kundenportal auf der Startseite); die übrigen Schritte folgen mit Client und Viewer.

1. *Sitzungen → Neue Ad-hoc-Sitzung*, optional eine Notiz (z. B. Ticketnummer) eingeben und *Sitzungscode erstellen* klicken. Der Code ist 10 Minuten gültig, einmalig verwendbar und wird nur einmal angezeigt. Nicht benötigte Codes können Sie widerrufen.
2. Den Kunden bitten, `https://support.example.com` zu öffnen und den Code einzugeben.
3. Der Kunde lädt den Client herunter und startet ihn; dieser zeigt Ihre Serveradresse und Ihren Anzeigenamen und fragt nach der Zustimmung.
4. Sitzung im Browser öffnen. In sensiblen Fällen den 6-stelligen **Sicherheitscode** am Telefon mit dem Kunden vergleichen – er beweist, dass niemand (auch kein kompromittierter Server) dazwischensitzt.
5. Werkzeuge: Monitorauswahl, Strg+Alt+Entf, Chat, Dateiübertragung, Zwischenablage, Remote-Shell, *Mit Administratorrechten neu starten* (Kunde bestätigt die UAC-Abfrage).
6. Sitzung beenden. Der Client entfernt sich selbst.

## 7. Sicherheitsverwaltung

### 7.1 IP-Sperren

Fehlgeschlagene Anmeldungen, falsche 2FA-Codes, falsche Sitzungscodes, ungültige Registrierungs-Tokens und ungültige Gerätesignaturen werden je IP-Adresse gezählt (IPv6 je /64-Netz). Standard: **5 Fehlversuche innerhalb von 10 Minuten → 7 Tage gesperrt.**

*IP-Sperren* (obere Navigation): aktive Sperren ansehen, Sperre aufheben, Netz manuell sperren (Dauer in Tagen, 0 = dauerhaft), Allow- und Deny-Einträge pflegen. Einträge aus der Konfiguration werden schreibgeschützt angezeigt.

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

## 8. E-Mail-Benachrichtigungen

Einrichtung über Umgebungsvariablen; *Einstellungen* (obere Navigation) zeigt die wirksamen Werte und sendet Ihnen eine Test-E-Mail:

```ini
Mkits__Smtp__Host=smtp.example.com
Mkits__Smtp__Port=587
Mkits__Smtp__Security=StartTls        # StartTls | SslOnConnect | None
Mkits__Smtp__UserName=mkits@example.com
Mkits__Smtp__Password=…               # besser: Docker-Secret / .env mit chmod 600
Mkits__Smtp__From=MKITS Support <mkits@example.com>
```

Benachrichtigungen gehen an jeden aktiven Admin mit E-Mail-Adresse, in dessen Oberflächensprache. Derzeit: automatische IP-Sperren. Geplant: neue Geräte warten auf Freigabe, fehlgeschlagene Integritätsprüfung, Server-Update verfügbar. Es werden höchstens `Mkits__Smtp__MaxNotificationsPerHour` (Standard 20) E-Mails pro Stunde versendet, damit ein Angriff Ihr Postfach nicht flutet.

## 9. Audit-Log und Aufzeichnungen

- *Audit-Log* (obere Navigation): Filter nach Ereignistyp und Freitext (IP, IDs, Details), neueste zuerst, 50 je Seite. Das Log ist per Hash-Kette verkettet; *Integrität prüfen* erkennt veränderte oder gelöschte Einträge. *(Export als CSV/JSON: geplant.)*
- *Aufzeichnungen* *(geplant)*: abspielen, herunterladen oder löschen. Das Abspielen wird protokolliert. Aufzeichnungen werden nach Ablauf der Aufbewahrungsfrist automatisch gelöscht.

## 10. Einstellungsreferenz

Umgebungsvariablen (`Abschnitt__Schlüssel`) haben Vorrang vor Einstellungen aus der Admin-Oberfläche. ✅ = heute verfügbar, 🔜 = geplant (Name kann sich bis zur Freigabe der Funktion noch ändern).

| Einstellung | Standard | | Beschreibung |
|---|---|---|---|
| `Mkits__PublicBaseUrl` | – | ✅ | öffentliche URL des Servers |
| `Mkits__DataDirectory` | `/data` | ✅ | Datenverzeichnis |
| `Mkits__ReverseProxy__KnownProxies__N` | – | ✅ | vertrauenswürdige Proxy-IPs |
| `Mkits__ReverseProxy__KnownNetworks__N` | – | ✅ | vertrauenswürdige Proxy-Netze (CIDR) |
| `Mkits__ReverseProxy__ForwardLimit` | `1` | ✅ | Proxy-Stufen |
| `Mkits__IpBan__MaxFailures` | `5` | ✅ | Fehlversuche bis zur Sperre |
| `Mkits__IpBan__Window` | `00:10:00` | ✅ | Zählzeitraum |
| `Mkits__IpBan__BanDuration` | `7.00:00:00` | ✅ | Sperrdauer (t.hh:mm:ss) |
| `Mkits__IpBan__IPv6PrefixLength` | `64` | ✅ | IPv6-Zusammenfassung |
| `Mkits__IpBan__AllowList__N` / `DenyList__N` | – | ✅ | CIDR-Listen (zusätzlich zur Admin-Oberfläche) |
| `Mkits__Sessions__CodeLength` | `8` | ✅ | Stellen des Adhoc-Codes (6–12) |
| `Mkits__Sessions__CodeLifetime` | `00:10:00` | ✅ | Gültigkeit unbenutzter Codes |
| `Mkits__Sessions__MaxActiveCodes` | `50` | ✅ | serverweite Grenze offener Codes (bei 8 Stellen höchstens 1000) |
| `Mkits__Sessions__MaxActiveCodesPerUser` | `10` | ✅ | offene Codes je Supporter |
| `Mkits__Sessions__ConsentTimeout` | `00:02:00` | 🔜 | Zeit für die Zustimmung am Gerät |
| `Mkits__Sessions__SuspendGracePeriod` | `00:01:00` | 🔜 | Wiederverbindungsfenster nach Verbindungsabbruch |
| `Mkits__Sessions__MaxDuration` | `08:00:00` | ✅ | maximale Sitzungsdauer |
| `Mkits__Security__RequireTwoFactorForUsers` | `false` | 🔜 | 2FA auch für User |
| `Mkits__Smtp__Host`, `__Port` (587), `__Security` (StartTls), `__UserName`, `__Password`, `__From` | – | ✅ | E-Mail (Abschnitt 8); leerer Host = deaktiviert |
| `Mkits__Smtp__MaxNotificationsPerHour` | `20` | ✅ | Schutz vor Mail-Fluten |
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
