# xpctl

Fernsteuerung für einen Windows-XP-Rechner aus dem Terminal. Der Server (`xpserver.py`, Python 3.4, nur Standardbibliothek) läuft sichtbar auf dem XP-Rechner, der Client (`xpctl.py`, Python 3.9+, ein `uv`-Skript) läuft auf einem modernen Rechner.

Gedacht für Retro-PCs: Programme und Installer starten, Dateien übertragen, Fenster und Dialoge lesen und bedienen, Registry und Einstellungen auslesen, Desktop-Verknüpfungen verwalten, CD-Images einhängen, CDs sichern, Treiber installieren, Spiele-Versionen bestimmen.

## Sicherheit

- Der Verkehr ist **nicht verschlüsselt**. Nur im vertrauenswürdigen LAN betreiben, nie ins Internet.
- Der Server führt auf Anforderung **beliebige Befehle** aus und liest und schreibt Dateien. Wer das Passwort kennt und im erlaubten Netz sitzt, hat damit volle Kontrolle über den Rechner.
- Die Anmeldung läuft per HMAC-SHA256 (Challenge/Response in beide Richtungen), das Passwort selbst geht nicht über die Leitung. Auf einem Netz mit Mitlesern ist das trotzdem kein Schutz für die übertragenen Daten.
- Erlaubte Netze müssen konfiguriert werden, ohne Angabe startet der Server nicht.
- `productkey` schickt den Produktschlüssel einmal unverschlüsselt durchs Netz und schreibt ihn clientseitig nur in eine Datei (Modus 600).

## Einrichtung

Auf dem XP-Rechner:

1. Python 3.4.4 installieren (die letzte Version für XP).
2. `xpserver.py`, `xpserver.bat` und eine `xpserver.json` (Vorlage: `xpserver.example.json`) in einen Ordner kopieren.
3. Firewall-Ausnahme für TCP 5050, zum Beispiel in `cmd`:
   `netsh firewall add portopening protocol=TCP port=5050 name=xpserver mode=ENABLE scope=SUBNET`
4. `xpserver.bat` doppelklicken und ein Passwort eintippen. Liegt eine Datei `xpctl.key` neben der `.bat`, startet der Server ohne Abfrage (z. B. per Autostart). Das Fenster bleibt offen und loggt jeden Befehl (auch in `xpserver.log`).

Auf dem Client:

    export XPCTL_HOST=192.168.1.50       # Adresse des XP-Rechners
    export XPCTL_KEY=...                 # oder Datei, siehe Tabelle
    ./xpctl.py ping

## Konfiguration

Der Server liest `xpserver.json` neben `xpserver.py` (anderer Ort: `--config` oder `XPCTL_CONFIG`). Es gibt keine eingebauten Pfade. Funktionen, die einen Eintrag brauchen, melden ohne ihn `nicht konfiguriert: ...`.

| Schlüssel | Bedeutung | Ohne Eintrag |
|---|---|---|
| `allow` | erlaubte Netze (CIDR). Alternativ `--allow` oder `XPCTL_ALLOW` (kommagetrennt) | Server startet nicht |
| `rm_roots` | Ordner, in denen `rm` ohne `--force` löschen darf (die Desktops sind immer erlaubt) | `rm` nur mit `--force` |
| `games_dir` | Ordner mit den Spielen, je Unterordner ein Spiel (für `games`) | Ordner der Exe gilt als Spielordner |
| `cd_drive` | CD/DVD-Laufwerk, z. B. `E:` | erstes CD-Laufwerk |
| `image_drive` | virtuelles Alcohol-Laufwerk, z. B. `G:` | `mount`/`unmount` melden "nicht konfiguriert" |
| `alcohol_axcmd` | Pfad zu `AxCmd.exe` von Alcohol 120% | `mount`/`unmount` melden "nicht konfiguriert" |
| `launcher_script` | eigenes VBS, das ein Image einhängt und eine Exe startet (Argumente: Image, Ordner, Exe). Gehört nicht zu diesem Repo | `lnk launcher` meldet "nicht konfiguriert" |
| `launcher_backup_dir` | Ordner, in den `lnk launcher` eine vorhandene Verknüpfung sichert | keine Sicherung |

Client und Umgebung:

| Variable / Option | Bedeutung |
|---|---|
| `XPCTL_HOST`, `--host` | Adresse des XP-Rechners (Pflicht) |
| `XPCTL_PORT`, `--port` | Port, Standard 5050 |
| `XPCTL_KEY` | Passwort |
| `XPCTL_KEY_FILE` | Passwortdatei, Standard: `key` im Ordner `xpctl` der Benutzer-Konfiguration |
| `XPCTL_SHOTS` | Ordner für Screenshots, Standard `xpctl-shots` im Home-Ordner (`shot --out` überschreibt) |
| `XPCTL_GROUPS` | Zuordnungsdatei für Desktop-Gruppen (Beispiel: `desktop-gruppen.example.txt`), oder `desk group --map DATEI` |
| `XPCTL_GOG_SSH`, `XPCTL_GOG_DIR` | SSH-Host und Ordner mit `<slug>/setup_<slug>_<version>_(<build>).exe` für `games --gog-ssh` |
| `XPCTL_SUPERVISED` | setzt `xpserver.bat` selbst, nötig für `update` |
| `PYTHON` | Interpreter für `xpserver.bat`, Standard `python` aus dem PATH |

## Benutzung

    ./xpctl.py run tasklist
    ./xpctl.py run --timeout 300 dir D:\
    ./xpctl.py run --detach D:\setup.exe       # GUI/Installer starten, nicht warten
    ./xpctl.py put setup.exe D:\Temp\          # Ordner mit \ am Ende, --mkdir legt ihn an
    ./xpctl.py get D:\Temp\install.log .
    ./xpctl.py update                          # xpserver.py auf dem XP ersetzen, Server startet neu

`run`: stdin ist leer. Installer, die auf Eingaben warten, laufen in den Timeout (Standard 60 s), danach wird der Prozessbaum beendet. Der Exit-Code wird durchgereicht, Timeout ist 124. Die Ausgabe wird mit der OEM-Codepage des XP-Rechners dekodiert.

### Dateien, Prozesse, Discs

    ./xpctl.py ls PFAD [-r [TIEFE]] [-p *.exe *.iso] [--files]
    ./xpctl.py cat DATEI [-g TEXT|/regex/] [--head N] [--tail N]
    ./xpctl.py ps [FILTER] [--cmdline]
    ./xpctl.py kill NAME|PID... [--tree] [--force]
    ./xpctl.py hash DATEI... [--sha1]
    ./xpctl.py copy QUELLE ZIEL [--verify]     # meldet unlesbare Dateien, md5-Vergleich
    ./xpctl.py rm PFAD... [-r] [--force]
    ./xpctl.py disc [E:]                       # Label, Kopierschutz-Dateien, PE-Sektionen
    ./xpctl.py ripiso ZIEL.iso [--drive E:]    # ungeschützte CD 1:1 als ISO (md5)
    ./xpctl.py pesec EXE...                    # PE-Sektionen (stxt774/stxt371 = SafeDisc 2+, .securom)
    ./xpctl.py mount IMAGE | unmount | eject | vol

`mount` und `unmount` verweigern, wenn ein anderes Programm als Explorer, Alcohol, cmd oder Python im Vordergrund ist (dann spielt vermutlich jemand und das Laufwerk gehört dem Spiel). `--force` übergeht das.

### Inventar und Einstellungen (nur lesen)

    ./xpctl.py updates [--grep .NET] [--json]  # Hotfixes: KB, Datum, Kategorie, SP-Stand, POSReady-Hack
    ./xpctl.py programs [--all] [--grep TEXT]
    ./xpctl.py sysinfo                         # Windows, CPU, RAM, Laufwerke, IE, DirectX, .NET
    ./xpctl.py reg 'HKCU\Control Panel\Mouse' [--recurse 2] [--values] [--json]
    ./xpctl.py settings [--only region,users] [--paths]
    ./xpctl.py setupapi [-g TEXT] [--last N]   # letzte Treiberinstallationen
    ./xpctl.py uninstall NAME [--run]          # Uninstall-Eintrag zeigen, mit --run Deinstaller starten
    ./xpctl.py productkey --out DATEI          # nur in Datei, im Terminal nur "Format ok (5x5)"

`settings` zeigt je Eintrag den Ist-Wert und, wenn bekannt, die Bedeutung. Abschnitte: `explorer design region users quiet network gaming hardware`. Kennwörter werden nie ausgegeben, nur "hinterlegt: ja/nein". HKCU ist der Benutzer, unter dem der Server läuft. Nicht gesetzte Werte heißen "Windows-Standard", nicht "aus".

### Fenster sehen und bedienen

Erst Text, nur im Notfall ein Bild:

    ./xpctl.py ui                      # Fenster + Controls des Vordergrundfensters, je Zeile Klick-Mitte @x,y
    ./xpctl.py ui --hwnd 0x1a2b
    ./xpctl.py press 0x1a2b            # Button per Handle
    ./xpctl.py press --label "Weiter >" --window Setup   # Button per Beschriftung, zeigt danach den nächsten Dialog
    ./xpctl.py settext 0x1a2c 'D:\Spiele\Foo'
    ./xpctl.py click 540 572 [--right] [--double] [--move]
    ./xpctl.py type 'text'
    ./xpctl.py key ALT+F4 ENTER
    ./xpctl.py focus 0x1a2b | close 0x1a2b
    ./xpctl.py shot [--hwnd H] [--region l,t,r,b] [--scale 0.5 | --full] [--out DATEI]

`ui` markiert hängende Fenster (`HÄNGT`), gesperrte Controls (`aus`) und Häkchen (`an`/`leer`). Zeigt `ui` keine Controls, ist das Fenster selbstgezeichnet: dann `shot` und mit `click` auf Bildkoordinaten arbeiten. Vollbild-DirectX-Spiele liefern im Screenshot oft Schwarz.

### Verknüpfungen und Desktop

    ./xpctl.py lnk show NAME
    ./xpctl.py lnk make NAME ZIEL [--args ...] [--icon Datei,0] [--folder GRUPPE | --no-group]
    ./xpctl.py lnk launcher NAME IMAGE SPIELORDNER EXE      # braucht launcher_script
    ./xpctl.py lnk icon NAME Datei,0 | lnk style NAME normal|max|min
    ./xpctl.py desk list [teil] | move NAME GRUPPE | foldericon GRUPPE Datei,0 | group [--map DATEI] | ungroup
    ./xpctl.py deskpos list | move NAME x y | grid NAME spalte zeile | row ZEILE NAMEN... | row ZEILE --rest

`NAME` ist der Dateiname ohne `.lnk` (ein eindeutiger Anfang reicht) oder ein voller Pfad. Verknüpfungen werden per IShellLink über ctypes geschrieben. `desk group` sortiert die Verknüpfungen des All-Users-Desktops anhand einer Zuordnungsdatei in Ordner (Format siehe `desktop-gruppen.example.txt`: `[Ordner]`, optional `icon=Datei,Index` oder `icon=@Verknüpfung`, danach je Zeile ein Namensanfang, der längste Treffer gewinnt).

### Treiber

    ./xpctl.py driver list [filter]            # angeschlossene Geräte: Name, läuft/Problemcode, Dienst, Hardware-ID
    ./xpctl.py driver install DATEI.inf [--hwid ID]

`driver install` nutzt SetupAPI. Unsignierte Treiber installiert der Dienst nicht selbst, XP öffnet dann beim Einstecken den Hardware-Assistenten (mit `press --label` durchklicken). Fehler `0xe000020b` heißt: Gerät gerade nicht angeschlossen.

### Spiele-Versionen

    ./xpctl.py games [filter] [--db DATEI] [--json DATEI] [--gog-ssh]

Pro Desktop-Verknüpfung zeigt `games` die Ziel-Exe, deren VERSIONINFO, Größe, Datum und md5, im Spielordner `goggame-*.info`, `gameinfo`, Versions- und Patchdateien sowie den passenden Uninstall-Eintrag.

Zusätzlich wird jede Exe gegen `game-versions.json` abgeglichen (per md5, sonst Exe-Name und Größe; teilen sich mehrere Spiele dieselbe Exe wie DOSBox, entscheidet der Spielname). Treffer erscheinen als `bekannt: <Version>, neuester: <Stand>` bzw. `(aktuell)`. Mit `--db` lässt sich eine andere Datei angeben.

Neue Einträge ergänzen: `games --json roh.json` schreibt je Spiel md5, Größe und Exe-Name. Die Werte in einen Eintrag der Form

    {"game": "Name", "year": "1999", "exe": "spiel.exe", "size": 123456, "md5": "...",
     "file_version": "1.0.0.0", "product_version": "1, 0, 0, 0",
     "version": "erkannte Version", "latest": "neuester bekannter Stand", "confidence": "hoch"}

unter `games` in `game-versions.json` eintragen. Die Datenbank ist von Hand recherchiert und kann Fehler enthalten, Ergänzungen und Korrekturen sind willkommen.

`--gog-ssh` ergänzt die GOG-Version aus den Setup-Dateinamen auf einem SSH-Host (`XPCTL_GOG_SSH`, `XPCTL_GOG_DIR`).

## Update des Servers

`xpctl.py update` schickt das lokale `xpserver.py` an den XP-Rechner. Der Server prüft Prüfsumme und Syntax, legt die alte Version als `xpserver.py.bak` ab und beendet sich mit Code 42, `xpserver.bat` startet ihn neu. Scheitert der Start, spielt die `.bat` die `.bak` zurück. Auch nach einem Absturz startet die `.bat` den Server neu (höchstens dreimal hintereinander, mit Rollback). Die `.bat` selbst wird nicht ferngeupdatet. `update` geht nur, wenn der Server über `xpserver.bat` gestartet wurde.

## Beigaben

- `wpchanger.pyw`: kleiner Wallpaper-Wechsler für den XP-Rechner. Aufruf `pythonw wpchanger.pyw ORDNER`, setzt alle 30 Minuten ein zufälliges BMP aus dem Ordner.
- `desktop-gruppen.example.txt`: Beispiel für `desk group`.
- `xpserver.example.json`: Beispiel für die Server-Konfiguration.

## Grenzen

- `run` nutzt `cmd.exe`. Listen und Baumansichten (SysListView32) zeigt `ui` nur als Control, nicht deren Einträge.
- Entwickelt gegen Python 3.15 auf dem Client und 3.4 auf dem XP-Rechner. Mindestversion des Servers nach `vermin`: 3.3.

## Lizenz

MIT, siehe `LICENSE`.

## Kopierschutz-Datenbank

`disc-protection.json` sammelt geprüfte Original-Discs: Volume-Label, Schutz (SafeDisc, SecuROM, StarForce, Label-Prüfung oder keiner), das Merkmal, an dem er erkannt wurde, und womit sich die Disc startfähig abbilden ließ (ISO oder MDF/MDS mit Alcohol-Datentyp). `xpctl.py disc` zeigt den passenden Eintrag zum eingelegten Label. `verified: false` heißt angenommen, nicht getestet. Ergänzungen per Pull Request sind willkommen.
