# xpctl (Client)

`xpctl.py` steuert einen Windows-XP-Rechner, auf dem `xpserver.exe` läuft. Ein einziges Python-Skript ohne Abhängigkeiten (Python 3.9 oder neuer), startbar mit `uv` oder direkt mit `python3`.

## Einrichten

    export XPCTL_HOST=192.168.1.50                 # Adresse des XP-Rechners
    mkdir -p ~/.config/xpctl
    printf '%s' 'meinpasswort' > ~/.config/xpctl/key && chmod 600 ~/.config/xpctl/key
    ./xpctl.py ping

Das Passwort ist dasselbe, das beim ersten Start von `xpserver.exe` auf dem XP-Rechner vergeben wurde.

| Variable / Option | Bedeutung |
|---|---|
| `XPCTL_HOST`, `--host` | Adresse des XP-Rechners (Pflicht) |
| `XPCTL_PORT`, `--port` | Port, Standard 5050 |
| `XPCTL_KEY` | Passwort |
| `XPCTL_KEY_FILE` | Passwortdatei, Standard `~/.config/xpctl/key` |
| `XPCTL_SHOTS` | Ordner für Screenshots, Standard `~/xpctl-shots` (`shot --out` überschreibt) |
| `XPCTL_GROUPS` | Zuordnungsdatei für Desktop-Gruppen (Beispiel: `desktop-gruppen.example.txt`), oder `desk group --map DATEI` |
| `XPCTL_GOG_SSH`, `XPCTL_GOG_DIR` | SSH-Host und Ordner mit `<slug>/setup_<slug>_<version>_(<build>).exe` für `games --gog-ssh` |

## Befehle

    ./xpctl.py run tasklist
    ./xpctl.py run --timeout 300 dir D:\
    ./xpctl.py run --detach D:\setup.exe       # GUI/Installer starten, nicht warten
    ./xpctl.py put setup.exe D:\Temp\          # Ordner mit \ am Ende, --mkdir legt ihn an
    ./xpctl.py get D:\Temp\install.log .
    ./xpctl.py update                          # xpserver.exe auf dem XP ersetzen, Server startet neu

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

`mount` und `unmount` verweigern, wenn ein anderes Programm als Explorer, Alcohol, cmd, Python oder xpserver im Vordergrund ist (dann spielt vermutlich jemand und das Laufwerk gehört dem Spiel). `--force` übergeht das.

### Inventar und Einstellungen (nur lesen)

    ./xpctl.py updates [--grep .NET] [--json]  # Hotfixes: KB, Datum, Kategorie, SP-Stand, POSReady-Hack
    ./xpctl.py programs [--all] [--grep TEXT]
    ./xpctl.py sysinfo                         # Windows, CPU, RAM, Laufwerke, IE, DirectX, .NET
    ./xpctl.py temps                           # Temperaturen: Mainboard (ACPI), Laufwerke (SMART), NVIDIA-GPU, HWiNFO, SpeedFan
    ./xpctl.py temps --source hwinfo --json    # nur eine Quelle, als JSON
    ./xpctl.py reg 'HKCU\Control Panel\Mouse' [--recurse 2] [--values] [--json]
    ./xpctl.py settings [--only region,users] [--paths]
    ./xpctl.py setupapi [-g TEXT] [--last N]   # letzte Treiberinstallationen
    ./xpctl.py uninstall NAME [--run]          # Uninstall-Eintrag zeigen, mit --run Deinstaller starten
    ./xpctl.py ver DATEI...                    # VERSIONINFO, Größe, Datum, md5 von Exe/DLL
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

`NAME` ist der Dateiname ohne `.lnk` (ein eindeutiger Anfang reicht) oder ein voller Pfad. Verknüpfungen werden per IShellLink geschrieben. `desk group` sortiert die Verknüpfungen des All-Users-Desktops anhand einer Zuordnungsdatei in Ordner (Format siehe `desktop-gruppen.example.txt`: `[Ordner]`, optional `icon=Datei,Index` oder `icon=@Verknüpfung`, danach je Zeile ein Namensanfang, der längste Treffer gewinnt).

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

### Installieren und Einstellen

    ./xpctl.py install '\\server\share\setup_spiel.exe' 'C:\Games\Spiel'   # Inno/NSIS/MSI still, Log geprüft
    ./xpctl.py install --detect setup.exe x              # nur Installer-Typ
    ./xpctl.py ini output 'C:\Games\X\dosbox.conf' -s sdl -v opengl   # INI/CONF-Wert setzen, Sicherung .xpctl-bak
    ./xpctl.py nv                                         # globale NVIDIA-3D-Einstellungen (NVAPI)
    ./xpctl.py nv vsync on                                # vsync app|on|off, triplebuffer on|off, prerender N
    ./xpctl.py toc                                        # Spuren der CD (Daten/Audio)
    ./xpctl.py ripcd 'C:\Rips\Spiel' 'Spiel'            # CD mit Musikspuren als CUE + ISO + BIN je Spur

`install` verweigert einen nicht leeren Zielordner (ohne `--force`), weil ein Deinstaller den ganzen Ordner löschen kann. Neuere GOG-Installer brechen still an der EULA-Checkbox ab: `install` startet sie dann interaktiv, hakt die Checkbox an und klickt sich bis zum Ende durch. `desk group` lädt den Desktop danach neu, damit geänderte Ordner-Icons sofort erscheinen.

### Spielstände

    ./xpctl.py saves                                  # welche bekannten Spiele installiert sind, Version, Profile
    ./xpctl.py saves formats                          # alle Spiel- und Versionsklassen
    ./xpctl.py saves nfsu                             # Profile mit Dateien und Prüfsummen-Status
    ./xpctl.py saves nfsu show Spieler [--json]       # alle Felder eines Profils
    ./xpctl.py saves nfsu set Spieler MOTIONBLUR=0 RES=4 [--dry-run]
    ./xpctl.py saves nfsu repair Spieler              # falsche Prüfsummen neu rechnen
    ./xpctl.py saves nfsu backup [Spieler]            # Profile unverändert sichern
    ./xpctl.py saves d2 show Held                     # Diablo II: Klasse, Level, Werte, Gold, Schwierigkeit
    ./xpctl.py saves d2 set Held strength=50 goldbank=100000 [--force]   # --force: Spiel steht im Hauptmenü

Unterstützt:

- Diablo II / Lord of Destruction 1.14d (`d2`): Charakter (`.d2s`) mit Klasse, Level, Status, Schwierigkeit, Attributen, Leben/Mana, Erfahrung und Gold. Schreibbar sind Attribute, freie Punkte und Gold, aber nur Werte, die schon in der Datei stehen (ein Wert 0 fehlt dort und müsste eingefügt werden). Level und Erfahrung bleiben nur lesbar. Die Prüfsumme wird beim Schreiben neu gerechnet. Im Spiel geprüft: ein mit `set strength=...` geänderter Charakter lädt und zeigt den neuen Wert.
- Need for Speed: Underground (`nfsu`): Grafik und Ton (`.cfg`) lesen und schreiben, Prüfsummen (Adler-32) von Optionen (`.opt`) und Karriere (`.ugd`) prüfen und reparieren, Profilliste (`Profiles.usr`). Das Online-Profil (`.pro`, enthält Zugangsdaten) wird nie angezeigt. Feldnamen dürfen kurz sein (`RES`), solange sie eindeutig sind, sonst mit Datei davor (`opt.checksum`).

Schreiben ist abgesichert:

- nur, wenn die md5 der Spiel-Exe zu einer geprüften Versionsklasse passt. Eine unbekannte Exe heißt: nur lesen.
- nicht, solange das Spiel läuft (es überschreibt seine Dateien beim Beenden).
- vorher kommen die alten Dateien nach `C:\xpctl\backup\saves\<spiel>\<Zeitstempel>`. Eine vorhandene Sicherung wird nie überschrieben.
- danach wird jede Datei zurückgelesen und Byte für Byte verglichen.

Im Spiel geprüft: NFSU lädt eine von `set` geänderte `.cfg` (Lichtschlieren an). Dabei muss `Speed.exe` beendet sein: das Spiel schreibt die Dateien beim Profilwechsel selbst neu, deshalb lehnt `saves` bei NFSU `--force` ab. Ob `--force` erlaubt ist, legt jede Spielklasse fest (`force_ok`, Begründung in `force_refusal`). Dass NFSU eine von `repair` reparierte `.opt` oder `.ugd` lädt, ist noch nicht geprüft.

**Neues Spiel oder neue Version.** Der Code liegt in `client/savegames/`:

- `core.py`: die Schnittstellen. `Remote` (Dateizugriff auf den XP-Rechner), `SaveFile` (eine Datei: `parse`, `serialize`, `fields`, `set`, `problems`, `repair`), `Profile`, `Game`.
- `nfsu.py`: Beispiel für ein Spiel.

Ein Spiel ist eine Basisklasse von `Game` mit `id`, `title`, `exe`, `locate` (Installationsordner, meist aus der Registry), `save_dir`, `profiles` und `load`. Ohne passende Versionsklasse darf sie nur lesen. Jede geprüfte Exe bekommt eine Unterklasse, deren Name die Version trägt, mit `version` und `exe_md5`:

```python
class NfsUnderground_20040115(NfsUnderground):
    version = "Speed.exe 15.01.2004"
    exe_md5 = frozenset({"95a0cba2466306469a33171e34995d05"})
```

Unterscheidet sich das Format zwischen Versionen, überschreibt die Versionsklasse nur das Nötige (z. B. `FILES` oder eine Dateiklasse). Das neue Modul in `savegames/__init__.py` importieren, dann erscheint es in `saves` und `saves formats`.

`serialize(parse(x))` muss Byte für Byte `x` ergeben: Bytes, deren Bedeutung unbekannt ist, werden unverändert durchgereicht. Tests mit erzeugten Dateien (keine echten Spielstände ins Repo):

    uv run --no-project python -m unittest discover -s client/tests
    XPCTL_SAVES_DIR=/tmp/kopien uv run --no-project python -m unittest discover -s client/tests   # plus echte Kopien

## Server aktualisieren

    ./xpctl.py update            # schickt server/dist/xpserver.exe (vorher: cd ../server && ./build.sh)
    ./xpctl.py migrate           # einmalig: alten Python-Server (xpserver.py + .bat) durch xpserver.exe ersetzen

`update` schickt die neue `xpserver.exe`. Der Server prüft Prüfsumme und Format, legt die alte Version als `xpserver.exe.bak` ab und übergibt an die neue. Kommt die neue nicht innerhalb von 30 Sekunden hoch, läuft die alte weiter und die Dateien werden zurückgetauscht.

`migrate` ist für Rechner, auf denen noch der alte Python-Server läuft. Es lädt `xpserver.exe` in denselben Ordner, legt `xpserver.json` (erlaubtes Netz: das /24 des Clients, `--allow` ändert das) und `xpctl.key` an, falls sie fehlen, richtet die Firewall-Freigabe ein, verschiebt einen Autostart-Eintrag für `xpserver.bat` aus dem Autostart-Ordner in den Server-Ordner (`autostart-alt-<name>.txt`), trägt `xpserver.exe` in `HKCU\...\Run` ein und schaltet dann um: Python-Server beenden, `xpserver.exe` starten, warten bis er antwortet. Die alten Dateien bleiben liegen.

## Beigaben

- `desktop-gruppen.example.txt`: Beispiel für `desk group`.
- `game-versions.json`: Versionsdatenbank für `games`.
- `disc-protection.json`: Kopierschutz-Datenbank für `disc`.
- `savegames/`: Spielstand-Formate für `saves`.

## Kopierschutz-Datenbank

`disc-protection.json` sammelt geprüfte Original-Discs: Volume-Label, Schutz (SafeDisc, SecuROM, StarForce, Label-Prüfung oder keiner), das Merkmal, an dem er erkannt wurde, und womit sich die Disc startfähig abbilden ließ (ISO oder MDF/MDS mit Alcohol-Datentyp). `xpctl.py disc` zeigt den passenden Eintrag zum eingelegten Label. `verified: false` heißt angenommen, nicht getestet. Ergänzungen per Pull Request sind willkommen.
