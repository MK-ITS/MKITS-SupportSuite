# Original requirements (German, verbatim)

> Source: project system prompt `_system_prompt.md`, 2026-10-17. Kept unchanged as the authoritative original.
> The English interpretation and all decisions are in [WORKPLAN.md](../../WORKPLAN.md) and [docs/adr/](../adr/README.md).

```text
<Produkt>
MKITS-SupportSuite
</Produkt>

<Ziel der Software>
- Fernwartungsoftware mit clientseitiger Unterstützung von Windows (ab Windows 10 64Bit / Sever 2016), Linux (Debian und Ubuntu), Android (ab Android Version 10) in Konsolen Sitzungen.
- Adhoc Einwahl für Endbenutzer per Client Software Download, nach Code Eingabe.
- Dauerhafte Verbindungen
</Ziel der Software>

<Technische Umsetzung>
- Serverkomponente soll unter Ubuntu Linux ab 26.04 betrieben werden.
- Serverkomponente für die Administration und client Einwahl als Web-Oberfläche umsetzen.
- Modular, gut wartbar und menschenleserlich programmiert.
</Technische Umsetzung>

<Sicherheitsstandards>
- Betrieb hinter einem ReverseProxy wie nginx für HTTPS vorgesehen.
- Wenn möglich sämtliche Kommunikation über https (443) leiten, falls dies nicht möglich ist, die Ports konfigurierbar machen.
- Interne Benutzerverwaltung mit den Rollen "Guest", "User" und "Admin".
-- "Guest" können unauthentifiziert eine Remote-Session starten (Kunden-Sicht).
-- "User" können authentifiziert eine Remote-Session starten (Kunden-Sicht) und als Supporter sich aufschalten.
-- "Admin" können alles was "User" können und zusätzlich dauerhafte feste Verbindungen zu Endgeräten einrichten.
- Schlagen Anmeldeversuche zu oft fehl (default 5 Versuche) wird die Quell-IP für eine Woche gesperrt (WhiteList und Blacklist konfigurierbar machen) - Vorbild "fail2ban".
</Sicherheitsstandards>

<Sonstiges>
- Als Vorlagen können bestehende Softwarelösungen wie ScreenConnect von ConnectWise oder RustDesk angesehen werden.
- Unter den verschiedenen Betriebssystemen aktuelle Sichtsvorkehrungen beachten (Windows UAC), da per remote Einwahl ggf. aministrative Tätigkeiten durchgeführt werden sollen.
- Sprachen der Oberflächen deutsch und englisch (je nach Benutzer CultureUI).
- Intern alles in englisch schreiben, dokumentieren und handhaben.
- Die Dokumentation soll späteren KI-Agenten helfen, das Projekt weiterzuentwickeln und zu pflegen.
</Sonstiges>

<Lizenz>
- AGPL 3
</Lizenz>
```
