# xpctl (client)

`xpctl.py` controls a Windows XP machine running `xpserver.exe`. A single Python script with no dependencies (Python 3.9 or newer), run it with `uv` or directly with `python3`.

CLI help and output are in German for now.

## Setup

    export XPCTL_HOST=192.168.1.50                 # address of the XP machine
    mkdir -p ~/.config/xpctl
    printf '%s' 'mypassword' > ~/.config/xpctl/key && chmod 600 ~/.config/xpctl/key
    ./xpctl.py ping

The password is the one you chose when you first started `xpserver.exe` on the XP machine.

| Variable / option | Meaning |
|---|---|
| `XPCTL_HOST`, `--host` | address of the XP machine (required) |
| `XPCTL_PORT`, `--port` | port, default 5050 |
| `XPCTL_KEY` | password |
| `XPCTL_KEY_FILE` | password file, default `~/.config/xpctl/key` |
| `XPCTL_SHOTS` | folder for screenshots, default `~/xpctl-shots` (`shot --out` overrides it) |
| `XPCTL_GROUPS` | mapping file for desktop groups (example: `desktop-gruppen.example.txt`), or `desk group --map FILE` |
| `XPCTL_GOG_SSH`, `XPCTL_GOG_DIR` | SSH host and folder containing `<slug>/setup_<slug>_<version>_(<build>).exe` for `games --gog-ssh` |

## Commands

    ./xpctl.py run tasklist
    ./xpctl.py run --timeout 300 dir D:\
    ./xpctl.py run --detach D:\setup.exe       # start a GUI/installer, don't wait
    ./xpctl.py put setup.exe D:\Temp\          # trailing \ means folder, --mkdir creates it
    ./xpctl.py get D:\Temp\install.log .
    ./xpctl.py update                          # replace xpserver.exe on the XP machine, server restarts

`run`: stdin is empty. Installers waiting for input run into the timeout (default 60 s), then the process tree is killed. The exit code is passed through, a timeout returns 124. Output is decoded with the XP machine's OEM code page.

### Files, processes, discs

    ./xpctl.py ls PATH [-r [DEPTH]] [-p *.exe *.iso] [--files]
    ./xpctl.py cat FILE [-g TEXT|/regex/] [--head N] [--tail N]
    ./xpctl.py ps [FILTER] [--cmdline]
    ./xpctl.py kill NAME|PID... [--tree] [--force]
    ./xpctl.py hash FILE... [--sha1]
    ./xpctl.py copy SOURCE TARGET [--verify]   # reports unreadable files, md5 comparison
    ./xpctl.py rm PATH... [-r] [--force]
    ./xpctl.py disc [E:]                       # label, copy protection files, PE sections
    ./xpctl.py ripiso TARGET.iso [--drive E:]  # unprotected CD 1:1 as ISO (md5)
    ./xpctl.py pesec EXE...                    # PE sections (stxt774/stxt371 = SafeDisc 2+, .securom)
    ./xpctl.py mount IMAGE | unmount | eject | vol

`mount` and `unmount` refuse when a program other than Explorer, Alcohol, cmd, Python or xpserver is in the foreground (then someone is probably playing and the drive belongs to the game). `--force` overrides this.

### Inventory and settings (read-only)

    ./xpctl.py updates [--grep .NET] [--json]  # hotfixes: KB, date, category, SP level, POSReady hack
    ./xpctl.py programs [--all] [--grep TEXT]
    ./xpctl.py sysinfo                         # Windows, CPU, RAM, drives, IE, DirectX, .NET
    ./xpctl.py temps                           # temperatures: mainboard (ACPI), drives (SMART), NVIDIA GPU, HWiNFO, SpeedFan
    ./xpctl.py temps --source hwinfo --json    # a single source, as JSON
    ./xpctl.py reg 'HKCU\Control Panel\Mouse' [--recurse 2] [--values] [--json]
    ./xpctl.py settings [--only region,users] [--paths]
    ./xpctl.py setupapi [-g TEXT] [--last N]   # recent driver installations
    ./xpctl.py uninstall NAME [--run]          # show the uninstall entry, --run starts the uninstaller
    ./xpctl.py ver FILE...                     # VERSIONINFO, size, date, md5 of an exe/DLL
    ./xpctl.py productkey --out FILE           # only to a file, the terminal just shows "Format ok (5x5)"

`settings` shows the current value of each entry and, where known, what it means. Sections: `explorer design region users quiet network gaming hardware`. Passwords are never printed, only "hinterlegt: ja/nein" (stored: yes/no). HKCU is the user the server runs as. Values that are not set show as `(nicht gesetzt)` (not set, so Windows uses its default), not as "off".

### Seeing and operating windows

Text first, a screenshot only when nothing else works:

    ./xpctl.py ui                      # windows + controls of the foreground window, each line with its click center @x,y
    ./xpctl.py ui --hwnd 0x1a2b
    ./xpctl.py press 0x1a2b            # button by handle
    ./xpctl.py press --label "Next >" --window Setup   # button by caption, then shows the next dialog
    ./xpctl.py settext 0x1a2c 'D:\Games\Foo'
    ./xpctl.py click 540 572 [--right] [--double] [--move]
    ./xpctl.py type 'text'
    ./xpctl.py key ALT+F4 ENTER
    ./xpctl.py focus 0x1a2b | close 0x1a2b
    ./xpctl.py shot [--hwnd H] [--region l,t,r,b] [--scale 0.5 | --full] [--out FILE]

`ui` marks hung windows (`HÄNGT`), disabled controls (`aus`) and checkboxes (`an`/`leer`, checked/empty). If `ui` shows no controls, the window draws itself: take a `shot` and `click` on image coordinates. Fullscreen DirectX games often come out black in screenshots.

### Shortcuts and desktop

    ./xpctl.py lnk show NAME
    ./xpctl.py lnk make NAME TARGET [--args ...] [--icon File,0] [--folder GROUP | --no-group]
    ./xpctl.py lnk launcher NAME IMAGE GAMEFOLDER EXE      # needs launcher_script
    ./xpctl.py lnk icon NAME File,0 | lnk style NAME normal|max|min
    ./xpctl.py desk list [part] | move NAME GROUP | foldericon GROUP File,0 | group [--map FILE] | ungroup
    ./xpctl.py deskpos list | move NAME x y | grid NAME column row | row ROW NAMES... | row ROW --rest

`NAME` is the file name without `.lnk` (a unique prefix is enough) or a full path. Shortcuts are written via IShellLink. `desk group` sorts the shortcuts on the All Users desktop into folders based on a mapping file (format see `desktop-gruppen.example.txt`: `[Folder]`, optionally `icon=File,Index` or `icon=@Shortcut`, then one name prefix per line, the longest match wins).

### Drivers

    ./xpctl.py driver list [filter]            # connected devices: name, running/problem code, service, hardware ID
    ./xpctl.py driver install FILE.inf [--hwid ID]

`driver install` uses SetupAPI. The service does not install unsigned drivers by itself, XP then opens the hardware wizard when the device is plugged in (click through it with `press --label`). Error `0xe000020b` means the device is not connected right now.

### Game versions

    ./xpctl.py gamecheck                   # games with a newer patch or unclear version, then the counters
    ./xpctl.py gamecheck --alle            # everything found, including up-to-date and unknown games
    ./xpctl.py gamecheck --push-db         # put game-versions.json next to xpserver.exe first
    ./xpctl.py games [filter] [--db FILE] [--json FILE] [--gog-ssh]

`gamecheck` (server v17) only looks for games that `game-versions.json` knows. It searches in this order: the GOG registry (`HKLM\SOFTWARE\GOG.com\Games`, one key for all GOG games, via `gog_id`), registry hints from the database (`reg`, for example Blizzard's `InstallPath`), desktop shortcuts. The first match uses exe name and size without reading the file; the md5 then confirms it in the background. The server caches md5 values in `xpserver-md5.tsv` next to the exe (keyed by path, size and modification time), so a second run reads no exe at all. Unknown exes are not hashed. Measured on an XP machine with 156 exes found: first run 2.0 s, then 0.3 s; loading the database takes 20 to 30 ms.

The status comes from the database field `status`: `aktuell` (up to date), `patch` (an official newer patch exists, community patches don't count) or `unklar` (unclear). A `?` in front of the version means a low-confidence match. The database is built into `xpserver.exe`. A `game-versions.json` next to the exe only wins if its `stand` (date) is newer, so an old file left over after an update does not shadow a newer database. The live window shows the same list in the "Spiele" (games) tab (see `server/README.md`).

For each desktop shortcut, `games` shows the target exe, its VERSIONINFO, size, date and md5, and from the game folder `goggame-*.info`, `gameinfo`, version and patch files plus the matching uninstall entry.

Each exe is also matched against `game-versions.json` (by md5, otherwise by exe name and size; when several games share the same exe, like DOSBox, the game name decides). Matches show up as `bekannt: <version>, neuester: <latest>` (known, latest) or `(aktuell)` (up to date). Use `--db` to point at a different file.

Adding entries: `games --json raw.json` writes md5, size and exe name for each game. Put the values into an entry like

    {"game": "Name", "year": "1999", "exe": "game.exe", "size": 123456, "md5": "...",
     "file_version": "1.0.0.0", "product_version": "1, 0, 0, 0",
     "version": "detected version", "latest": "latest known version", "status": "aktuell",
     "confidence": "hoch", "gog_id": "1207658...", "reg": ["HKLM\\SOFTWARE\\Company\\Game|InstallPath"],
     "probe": "Game.exe"}

under `games` in `game-versions.json` and set `stand` in the header to today's date. `confidence` is `hoch`, `mittel` or `niedrig` (high, medium, low). `gog_id`, `reg` and `probe` are optional; `probe` means the version lives in another file in the same folder (Diablo II: `Diablo II.exe` is only the launcher, `Game.exe` carries the version). `client/tests/test_gamedb.py` checks the fields and that exe name plus size stay unique. The database was researched by hand and may contain mistakes, additions and corrections are welcome.

`--gog-ssh` adds the GOG version from the setup file names on an SSH host (`XPCTL_GOG_SSH`, `XPCTL_GOG_DIR`).

### Installing and configuring

    ./xpctl.py install '\\server\share\setup_game.exe' 'C:\Games\Game'   # Inno/NSIS/MSI silent, log checked
    ./xpctl.py install --detect setup.exe x              # installer type only
    ./xpctl.py ini output 'C:\Games\X\dosbox.conf' -s sdl -v opengl   # set an INI/CONF value, backup as .xpctl-bak
    ./xpctl.py nv                                         # global NVIDIA 3D settings (NVAPI)
    ./xpctl.py nv vsync on                                # vsync app|on|off, triplebuffer on|off, prerender N
    ./xpctl.py toc                                        # CD tracks (data/audio)
    ./xpctl.py ripcd 'C:\Rips\Game' 'Game'               # CD with audio tracks as CUE + ISO + one BIN per track

`install` refuses a target folder that is not empty (unless `--force`), because an uninstaller may delete the whole folder. Newer GOG installers silently abort at the EULA checkbox: `install` then runs them interactively, ticks the checkbox and clicks through to the end. `desk group` reloads the desktop afterwards so changed folder icons show up right away.

### Save games

    ./xpctl.py saves                                  # which known games are installed, version, profiles
    ./xpctl.py saves formats                          # all game and version classes
    ./xpctl.py saves nfsu                             # profiles with files and checksum status
    ./xpctl.py saves nfsu show Player [--json]        # all fields of a profile
    ./xpctl.py saves nfsu set Player MOTIONBLUR=0 RES=4 [--dry-run]
    ./xpctl.py saves nfsu repair Player               # recompute wrong checksums
    ./xpctl.py saves nfsu backup [Player]             # back up profiles unchanged
    ./xpctl.py saves d2 show Hero                     # Diablo II: class, level, stats, gold, difficulty
    ./xpctl.py saves d2 set Hero strength=50 goldbank=100000 [--force]   # --force: game sits in the main menu

Supported:

- Diablo II / Lord of Destruction 1.14d (`d2`): character (`.d2s`) with class, level, status, difficulty, attributes, life/mana, experience and gold. Attributes, unspent points and gold are writable, but only values that already exist in the file (a value of 0 is missing there and would have to be inserted). Level and experience stay read-only. The checksum is recomputed on write. Tested in game: a character changed with `set strength=...` loads and shows the new value.
- Need for Speed: Underground (`nfsu`): read and write graphics and sound (`.cfg`), check and repair the checksums (Adler-32) of options (`.opt`) and career (`.ugd`), profile list (`Profiles.usr`). The online profile (`.pro`, contains login data) is never shown. Field names can be short (`RES`) as long as they are unique, otherwise prefix the file (`opt.checksum`).

Writing is guarded:

- only when the md5 of the game exe matches a tested version class. An unknown exe means read-only.
- not while the game is running (it overwrites its files on exit).
- the old files go to `C:\xpctl\backup\saves\<game>\<timestamp>` first. An existing backup is never overwritten.
- afterwards every file is read back and compared byte by byte.

Tested in game: NFSU loads a `.cfg` changed by `set` (light streaks on). `Speed.exe` must not be running for this: the game rewrites its files itself when switching profiles, so `saves` refuses `--force` for NFSU. Each game class decides whether `--force` is allowed (`force_ok`, reason in `force_refusal`). Whether NFSU loads an `.opt` or `.ugd` fixed by `repair` has not been tested yet.

**New game or new version.** The code lives in `client/savegames/`:

- `core.py`: the interfaces. `Remote` (file access on the XP machine), `SaveFile` (one file: `parse`, `serialize`, `fields`, `set`, `problems`, `repair`), `Profile`, `Game`.
- `nfsu.py`: example of a game.

A game is a base class derived from `Game` with `id`, `title`, `exe`, `locate` (install folder, usually from the registry), `save_dir`, `profiles` and `load`. Without a matching version class it may only read. Every tested exe gets a subclass whose name carries the version, with `version` and `exe_md5`:

```python
class NfsUnderground_20040115(NfsUnderground):
    version = "Speed.exe 15.01.2004"
    exe_md5 = frozenset({"95a0cba2466306469a33171e34995d05"})
```

If the format differs between versions, the version class only overrides what it has to (for example `FILES` or a file class). Import the new module in `savegames/__init__.py`, then it shows up in `saves` and `saves formats`.

`serialize(parse(x))` must give back `x` byte for byte: bytes with unknown meaning are passed through unchanged. Tests use generated files (no real save games in the repo):

    uv run --no-project python -m unittest discover -s client/tests
    XPCTL_SAVES_DIR=/tmp/copies uv run --no-project python -m unittest discover -s client/tests   # plus real copies

## Updating the server

    ./xpctl.py update            # sends server/dist/xpserver.exe (build first: cd ../server && ./build.sh)
    ./xpctl.py migrate           # once: replace the old Python server (xpserver.py + .bat) with xpserver.exe

`update` sends the new `xpserver.exe`. The server checks the checksum and format, keeps the old version as `xpserver.exe.bak` and hands over to the new one. If the new one does not come up within 30 seconds, the old one keeps running and the files are swapped back.

`migrate` is for machines still running the old Python server. It uploads `xpserver.exe` into the same folder, creates `xpserver.json` (allowed network: the client's /24, `--allow` changes that) and `xpctl.key` if they are missing, sets up the firewall rule, moves an autostart entry for `xpserver.bat` out of the Startup folder into the server folder (`autostart-alt-<name>.txt`), adds `xpserver.exe` to `HKCU\...\Run` and then switches over: stop the Python server, start `xpserver.exe`, wait until it answers. The old files stay where they are.

## Extras

- `desktop-gruppen.example.txt`: example for `desk group`.
- `game-versions.json`: version database for `games` and `gamecheck`.
- `disc-protection.json`: copy protection database for `disc`.
- `savegames/`: save game formats for `saves`.

## Copy protection database

`disc-protection.json` collects checked original discs: volume label, protection (SafeDisc, SecuROM, StarForce, label check or none), the marker it was detected by, and how the disc could be imaged so it still starts (ISO or MDF/MDS with an Alcohol data type). `xpctl.py disc` shows the entry matching the inserted label. `verified: false` means assumed, not tested. Additions via pull request are welcome.
