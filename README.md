# <img src="docs/img/symbol.png" width="40" align="top"> xpctl

<!-- links:start -->
[![Support me on Ko-fi](https://malura.de/assets/badge/kofi.svg)](https://malura.de/go/kofi?ref=xpctl)
[![Check out my page](https://malura.de/assets/badge/malura.svg)](https://malura.de/go/site?ref=xpctl)
<!-- links:end -->

Remote control for Windows XP machines from your terminal. Built for retro PCs: start programs and installers, transfer files, read and operate windows, read the registry and system settings, arrange desktop shortcuts, mount CD images, back up CDs, install drivers, identify game versions and edit save games.

| Live window: commands, errors, activity, log | Games tab: installed vs. latest version of every known game |
|---|---|
| ![xpserver live window](docs/img/live-fenster.png) | ![xpserver games tab](docs/img/spiele.png) |

The games tab checks every game the built-in version database knows (found via the GOG registry, registry hints and desktop shortcuts), matches it by file size first and confirms with an MD5 in the background. A second run takes about 0.3 s on a Pentium-era XP box. Same list in the terminal: `./xpctl.py gamecheck`.

Note: the program itself speaks German for now. The tray UI, CLI help, command output and the screenshots are in German, the docs are in English.

Two parts:

| Part | Runs on | What |
|---|---|---|
| [`server/`](server/) | Windows XP | `xpserver.exe`: tray icon with a live window, .NET 3.5, a single file, no installation |
| [`client/`](client/) | macOS, Linux, Windows | `xpctl.py`: a single Python script with no dependencies (Python 3.9+) |

## Five-minute setup

**On the XP machine**

1. Build `xpserver.exe`: `cd server && ./build.sh` (needs only the [dotnet SDK](https://dotnet.microsoft.com/download), works on macOS, Linux and Windows). Result: `server/dist/xpserver.exe`.
   The XP machine needs .NET Framework 3.5 (Windows XP does not ship with it, the installer is `dotnetfx35.exe` from Microsoft).
2. Put it in a folder, for example `C:\xpctl\`, and double-click it.
3. In the setup dialog, choose a password and click "Einrichten und starten" (set up and start). Network, autostart and firewall are prefilled.

**On your own machine**

    git clone https://github.com/maluramichael/xpctl && cd xpctl/client
    export XPCTL_HOST=192.168.1.50              # address of the XP machine
    mkdir -p ~/.config/xpctl && printf '%s' 'PASSWORD' > ~/.config/xpctl/key && chmod 600 ~/.config/xpctl/key
    ./xpctl.py ping
    ./xpctl.py run ver
    ./xpctl.py sysinfo

All commands: [`client/README.md`](client/README.md). Server, tray icon, configuration, updates: [`server/README.md`](server/README.md).

Still running the old Python server (`xpserver.py` + `xpserver.bat`)? `./xpctl.py migrate` switches the XP machine over to `xpserver.exe` in one step.

## Security

The server runs arbitrary commands on request. Anyone who knows the password and sits in an allowed network has full control over the XP machine. Traffic is not encrypted. The login uses HMAC-SHA256 challenge-response, so the password never goes over the wire. Only run it in your own LAN, never expose it to the internet.

## Folders

| Path | Contents |
|---|---|
| `client/` | `xpctl.py`, save game formats (`savegames/`) with tests, game version and copy protection databases, example for desktop groups |
| `server/` | Source of `xpserver.exe` (C#), `build.sh`, example config, tests, the old Python server under `legacy-python/` |
| `docs/` | [Protocol](docs/protocol.md) |
| `tools/` | `wpchanger.pyw`: small wallpaper changer for the XP machine (`pythonw wpchanger.pyw FOLDER`, a random BMP every 30 minutes) |

## License

MIT, see `LICENSE`.
