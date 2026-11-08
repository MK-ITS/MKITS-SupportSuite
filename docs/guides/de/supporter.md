# Handbuch für Supporter

Für alle, die mit MKITS Remote Support Kunden helfen: Sitzungen starten, im Viewer arbeiten und sich mit installierten
Geräten verbinden. Einrichtung und Richtlinien stehen im [Administrationshandbuch](administration.md).

Sie brauchen ein Benutzerkonto auf dem Server Ihrer Organisation (legt Ihr Admin an) und einen aktuellen Browser
(Chrome, Edge, Firefox oder Safari). Anmelden unter der Adresse Ihres Servers, z. B.
`https://support.example.com/account/login`.

## 1. Einem Kunden helfen (Ad-hoc-Sitzung)

1. Öffnen Sie **Sitzungen** und klicken Sie auf **Neue Ad-hoc-Sitzung**. Tragen Sie bei Bedarf eine Notiz ein
   (Ticketnummer, Kundenname) und klicken Sie auf **Sitzungscode erstellen**.
2. Nennen Sie dem Kunden den 8-stelligen Code, z. B. am Telefon. Er gilt **10 Minuten**, funktioniert **einmal** und
   wird Ihnen nur dieses eine Mal angezeigt. Nicht mehr benötigte Codes können Sie widerrufen.
3. Der Kunde öffnet die Startseite Ihres Servers, gibt den Code ein und startet das Support-Programm (siehe
   [Anleitung für Kunden](customer.md)). Das Programm zeigt Ihren Namen und die Adresse Ihres Servers und fragt den
   Kunden um Zustimmung.
4. Klicken Sie unter **Sitzungen** neben der Sitzung auf **Öffnen**; der Viewer öffnet sich in einem neuen Tab.
   Sobald der Kunde zugestimmt hat, sehen Sie den Bildschirm.
5. **Vergleichen Sie den Sicherheitscode** in der oberen Leiste des Viewers mit dem im Fenster des Kunden – zumindest,
   wenn es um Sensibles geht. Sind beide gleich, ist die Verbindung zwischen Ihnen beiden Ende-zu-Ende verschlüsselt –
   nicht einmal der Server kann mitlesen. Weichen sie ab, beenden Sie die Sitzung.
6. Wenn Sie fertig sind, klicken Sie auf **Sitzung beenden**. Auch der Kunde kann jederzeit beenden; das Programm
   schließt sich dann und hinterlässt keine Installation.

Mehrere Supporter können derselben Sitzung beitreten; der Kunde sieht alle, die verbunden sind.

## 2. Arbeiten im Viewer

| Werkzeug | Funktion |
|---|---|
| **Bildschirm** | Klicken Sie in den Bildschirm, um ihn zu bedienen: Maus, Mausrad und Tastatur gehen an den entfernten Computer (Tastaturbelegung der Gegenseite). Der Kunde sieht Ihre Mausbewegungen. |
| **Monitor** | Monitor wählen, wenn der entfernte Computer mehrere hat. |
| **Bildqualität** | *Geringe Bandbreite* (10 Bilder/s) für langsame Verbindungen, *Ausgewogen* (15 Bilder/s, Standard), *Beste Qualität* (30 Bilder/s, schärfer). Der Viewer nutzt H.264-Video, wo Browser und entfernter Computer es unterstützen, sonst Einzelbilder. |
| **1:1** | Zeigt den entfernten Bildschirm in Originalgröße zum Scrollen; sonst wird er ans Fenster angepasst. |
| **Ctrl+Alt+Del, Win, Alt+Tab** | Tastenkombinationen, die sonst Ihr eigener Computer abfangen würde. Ctrl+Alt+Del (Strg+Alt+Entf) braucht den Administratormodus (ad hoc) oder einen installierten Agent. |
| **Chat** | Textnachrichten mit dem Kunden. |
| **Dateien** | *Datei senden* bietet dem Kunden eine Datei an; er muss annehmen, sie landet in seinem Ordner *Downloads*. Dateien des Kunden erscheinen mit *speichern*. |
| **Zwischenablage** | *Zwischenablage senden* überträgt Ihren Text in die Zwischenablage des entfernten Computers, *Zwischenablage des Kunden kopieren* holt seinen (der Browser fragt eventuell um Erlaubnis). |
| **Terminal** | Eine Kommandozeile auf dem entfernten Computer (PowerShell unter Windows, die Login-Shell unter Linux) mit den Rechten des angemeldeten Benutzers. Mehrere Terminals sind möglich. |
| **Admin-Terminal** | Eine Kommandozeile mit Administratorrechten – SYSTEM unter Windows, root unter Linux. Siehe unten. |

Bildqualität und *1:1* merkt sich der Viewer in Ihrem Browser.

### Administratorrechte

- **Ad-hoc-Sitzungen (Windows):** Der Kunde klickt in seinem Fenster auf **Mit Administratorrechten neu starten** und
  bestätigt die Windows-Abfrage (UAC). Dann sehen und bedienen Sie auch Sicherheitsabfragen, den Sperrbildschirm und
  Strg+Alt+Entf, und das *Admin-Terminal* funktioniert. Der Modus endet mit der Sitzung.
- **Installierte Geräte:** Das *Admin-Terminal* braucht die Berechtigung **Admin-Shell (SYSTEM/root)** für die Gruppe
  des Geräts (vergibt Ihr Admin). Ohne sie wird die Anfrage abgelehnt – Sie bekommen nie unbemerkt eine schwächere
  Shell.

## 3. Installierte Geräte (dauerhafter Zugriff)

Computer mit installiertem Agent erscheinen unter **Geräte**, soweit Ihre Gruppen es erlauben.

1. Wählen Sie das Gerät und klicken Sie auf **Verbinden** (es muss online sein). Ist schon ein Kollege verbunden,
   nehmen Sie mit **Sitzung beitreten** an dieser Sitzung teil.
2. Je nach Richtlinie der Gruppe startet die Sitzung sofort, der angemeldete Benutzer wird nur informiert, oder er wird
   vorher um Zustimmung gefragt (wie bei einer Ad-hoc-Sitzung).
3. Sie arbeiten im selben Viewer wie oben. Was Sie dürfen – ansehen, bedienen, Dateien, Zwischenablage, Terminal,
   Admin-Terminal – hängt von Ihren Berechtigungen für diese Gruppe ab; Schaltflächen für Nicht-Erlaubtes bleiben
   ohne Wirkung.

Niemand angemeldet? Unter Windows sehen Sie den Anmeldebildschirm und können sich anmelden; unter Linux den
Anmeldebildschirm, wenn er unter X11 läuft. Admin-Terminals funktionieren auch ohne angemeldeten Benutzer.

## 4. Gute Praxis

- Codes nur dem Kunden geben, mit dem Sie gerade sprechen, und nur wenn er ihn braucht.
- Sicherheitscode vergleichen, bevor Sie mit Passwörtern oder sensiblen Daten arbeiten.
- Dem Kunden sagen, bevor Sie Dateien oder Terminals öffnen, und die Sitzung beenden, sobald Sie fertig sind.
- Alles Wesentliche (Sitzungen, Zustimmungen, Dateiübertragungen, Terminals) steht im Audit-Log.

## 5. Probleme lösen

| Problem | Lösung |
|---|---|
| Der Code des Kunden ist „nicht gültig“ oder „abgelaufen“ | Neuen Code erstellen – Codes gelten einmal und 10 Minuten. |
| Der Viewer bleibt bei „Verbinde …“ | Der Kunde hat das Programm noch nicht gestartet oder noch nicht zugestimmt; nachfragen. Nach einer Netzunterbrechung die Seite neu laden. |
| Das Bild ruckelt | *Geringe Bandbreite* wählen; andere Tabs mit Video schließen. |
| *Admin-Terminal* „nicht verfügbar“ | Ad hoc: Der Kunde muss mit Administratorrechten neu starten. Installiertes Gerät: Berechtigung *Admin-Shell* beim Admin anfragen. |
| Zwischenablage-Schaltflächen tun nichts | Der Browser blockiert den Zugriff; für die Seite erlauben. |
