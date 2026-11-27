#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.9"
# ///
"""xpctl: Client für xpserver auf dem Retro-XP-Rechner.

  xpctl.py ping
  xpctl.py run [--timeout 60] [--cwd DIR] [--detach] BEFEHL...
  xpctl.py put LOKAL REMOTE      (REMOTE = Datei oder Ordner mit \\ am Ende)
  xpctl.py get REMOTE LOKAL
  xpctl.py ui [--hwnd H] [--all]          Fenster + Controls als Text mit Klick-Koordinaten (zuerst)
  xpctl.py shot [--hwnd H] [--region l,t,r,b] [--scale 0.5|--full]   PNG (nur wenn ui nicht reicht)
  xpctl.py click X Y [--right] [--double] | press HWND | settext HWND TEXT
  xpctl.py type TEXT | key ENTER ALT+F4 ... | focus HWND | close HWND
  xpctl.py updates [--grep TEXT] [--json]      Hotfixes/Updates, SP-Stand, POSReady
  xpctl.py programs [--all] [--grep TEXT] [--json]   installierte Programme
  xpctl.py sysinfo [--json]                    Windows, CPU, RAM, Laufwerke, IE, DirectX, .NET
  xpctl.py temps [--source acpi,smart,nvidia,hwinfo,speedfan] [--json]   Temperaturen (xpserver.exe)
  xpctl.py saves [SPIEL [show|set|repair|backup] [PROFIL] [FELD=WERT ...]] [--json] [--dry-run]
                                               Spielstände und Profile (client/savegames)
  xpctl.py reg KEY [--values] [--recurse N] [--json]   Registry-Schlüssel lesen (HKLM/HKCU/HKU/HKCR)
  xpctl.py settings [--only abschnitt,...] [--paths] [--json]   kuratierte Einstellungen
  xpctl.py productkey --out DATEI              XP-Produktschlüssel nur in Datei (chmod 600)
  xpctl.py update [--file xpserver.exe]  (Server auf dem XP ersetzen + neu starten)
  xpctl.py migrate                       (alten Python-Server durch xpserver.exe ersetzen)
  Spiele-Werkzeuge (Server v6):
  xpctl.py press --label "Weiter >" [--window REGEX]   Button per Beschriftung, zeigt danach den Dialog
  xpctl.py lnk show|make|launcher|icon|style ...       Verknüpfungen
  xpctl.py desk list|move|foldericon|group|ungroup     Gruppenordner nach desktop-gruppen.txt
  xpctl.py deskpos list|move|grid|row                  Desktop-Icons verschieben
  xpctl.py mount IMG | unmount | eject | vol           Alcohol-Laufwerk G:, CD-Laufwerk E:
  xpctl.py ripiso ZIEL.iso | pesec EXE...                CD als ISO lesen, Kopierschutz-Sektionen
  xpctl.py driver list [FILTER] | install INF [--hwid ID]   Geräte, INF-Treiber (Server v7)
  xpctl.py games [FILTER] [--db DATEI] [--json DATEI]            Spiele-Icons: Exe-Version, GOG-Info, Uninstall (Server v8)
  xpctl.py gamecheck [--alle] [--fast] [--push-db]               bekannte Spiele: installiert vs. neuester Stand (Server v17)
  Dateien/Prozesse/Discs (Server v9, statt run mit dir/findstr/type/tasklist/taskkill/xcopy/del):
  xpctl.py ls PFAD [-r [TIEFE]] [-p MUSTER...] [--files]   Größe, Datum, Summe
  xpctl.py cat DATEI [-g TEXT|/regex/] [--head N] [--tail N]
  xpctl.py ps [FILTER] [--cmdline] | kill NAME|PID... [--tree] [--force]
  xpctl.py hash DATEI... [--sha1] | copy QUELLE ZIEL [--verify] | rm PFAD... [-r] [--force]
  xpctl.py disc [E:]          Label, Wurzel, Kopierschutz-Dateien, PE-Sektionen der Exes
  xpctl.py setupapi [-g TEXT] [--last N]   letzte Treiberinstallationen aus setupapi.log
  xpctl.py uninstall NAME [--run]          Uninstall-Eintrag zeigen, mit --run Deinstaller starten
  xpctl.py ver DATEI...                    VERSIONINFO, Größe, Datum, md5
  xpctl.py toc [--drive E:] | ripcd ORDNER NAME   Spuren zeigen / Mixed-Mode-CD als CUE+ISO+BIN rippen
  xpctl.py nv [vsync on|off|app] [triplebuffer on|off]  globale NVIDIA-3D-Einstellungen
  xpctl.py ini KEY DATEI... [-s ABSCHNITT] [-v WERT]   INI/CONF-Wert lesen/setzen
  xpctl.py install SETUP ZIEL [--kind] [--force]   Installer still (Inno/NSIS/MSI, GOG-EULA), Log geprüft

Host: --host oder XPCTL_HOST (Pflicht), Port 5050 (XPCTL_PORT).
Passwort: XPCTL_KEY oder die Datei aus XPCTL_KEY_FILE (Standard: Datei "key" im Ordner xpctl der Benutzer-Konfiguration).
Screenshots: --out oder XPCTL_SHOTS (Ordner, sonst xpctl-shots im Home-Ordner).
GOG-Versionen per SSH (games --gog-ssh): XPCTL_GOG_SSH (SSH-Host), XPCTL_GOG_DIR (Ordner mit <slug>/setup_*.exe).
Desktop-Gruppen: --map DATEI oder XPCTL_GROUPS (Zuordnungsdatei, Beispiel desktop-gruppen.example.txt).
"""
import argparse
import hashlib
import hmac
import json
import math
import os
import re
import socket
import struct
import sys
import time
import zlib
from pathlib import Path

MAX_FRAME = 16 * 1024 * 1024
CHUNK = 256 * 1024


def recv_exact(sock, n):
    buf = bytearray()
    while len(buf) < n:
        chunk = sock.recv(min(n - len(buf), 1024 * 1024))
        if not chunk:
            raise EOFError("Verbindung geschlossen")
        buf.extend(chunk)
    return bytes(buf)


def send_frame(sock, data):
    sock.sendall(struct.pack(">I", len(data)) + data)


def recv_frame(sock):
    (n,) = struct.unpack(">I", recv_exact(sock, 4))
    if n > MAX_FRAME:
        raise ValueError(f"Frame zu groß: {n}")
    return recv_exact(sock, n)


def send_json(sock, obj):
    send_frame(sock, json.dumps(obj).encode("utf-8"))


def recv_json(sock):
    return json.loads(recv_frame(sock).decode("utf-8"))


def mac(key: bytes, role: bytes, nonce: str) -> str:
    return hmac.new(key, role + b"|" + nonce.encode("ascii"), hashlib.sha256).hexdigest()


def load_key() -> bytes:
    k = os.environ.get("XPCTL_KEY")
    if k:
        return k.strip().encode()
    p = Path(os.environ.get("XPCTL_KEY_FILE") or Path.home() / ".config" / "xpctl" / "key")
    if p.exists():
        return p.read_bytes().strip()
    sys.exit(f"Kein Passwort: XPCTL_KEY setzen oder Datei {p} anlegen (XPCTL_KEY_FILE ändert den Pfad).")


def connect(host: str, port: int, key: bytes) -> socket.socket:
    sock = socket.create_connection((host, port), timeout=10)
    hello = recv_json(sock)
    nonce = hello["nonce"]
    cnonce = os.urandom(16).hex()
    send_json(sock, {"auth": mac(key, b"client", nonce), "cnonce": cnonce})
    resp = recv_json(sock)
    if not resp.get("ok"):
        sys.exit("Anmeldung abgelehnt (falsches Passwort?)")
    if not hmac.compare_digest(resp.get("auth", ""), mac(key, b"server", cnonce)):
        sys.exit("Server konnte sich nicht ausweisen, Abbruch.")
    sock.settimeout(None)
    return sock


def cmd_ping(sock, args):
    send_json(sock, {"op": "ping"})
    r = recv_json(sock)
    for k, v in r.items():
        if k != "ok":
            print(f"{k}: {v}")
    return 0


def cmd_run(sock, args):
    req = {"op": "run", "cmd": " ".join(args.command), "timeout": args.timeout}
    if args.cwd:
        req["cwd"] = args.cwd
    if args.detach:
        req["detach"] = True
    send_json(sock, req)
    r = recv_json(sock)
    if not r.get("ok"):
        print("Fehler:", r.get("error"), file=sys.stderr)
        return 1
    if r.get("detached"):
        print(f"gestartet (pid {r['pid']}, das ist die cmd.exe)")
        return 0
    sys.stdout.write(r["stdout"])
    sys.stderr.write(r["stderr"])
    if r["timed_out"]:
        print(f"[Timeout nach {args.timeout:g}s, Prozess beendet]", file=sys.stderr)
        return 124
    return r["exit"]


def cmd_put(sock, args):
    src = Path(args.local)
    if not src.is_file():
        sys.exit(f"keine Datei: {src}")
    size = src.stat().st_size
    send_json(sock, {"op": "put", "path": args.remote, "size": size,
                     "name": src.name, "mkdir": args.mkdir})
    r = recv_json(sock)
    if not r.get("ready"):
        print("Fehler:", r.get("error"), file=sys.stderr)
        return 1
    h = hashlib.sha256()
    with src.open("rb") as f:
        while True:
            data = f.read(CHUNK)
            if not data:
                break
            h.update(data)
            send_frame(sock, data)
    r = recv_json(sock)
    if not r.get("ok"):
        print("Fehler:", r.get("error"), file=sys.stderr)
        return 1
    if r["sha256"] != h.hexdigest():
        print("Prüfsumme stimmt nicht!", file=sys.stderr)
        return 1
    print(f"{size} Bytes -> {r['target']} (sha256 ok)")
    return 0


def cmd_get(sock, args):
    send_json(sock, {"op": "get", "path": args.remote})
    r = recv_json(sock)
    if not r.get("ready"):
        print("Fehler:", r.get("error"), file=sys.stderr)
        return 1
    size = r["size"]
    dst = Path(args.local)
    if dst.is_dir():
        dst = dst / args.remote.replace("\\", "/").rsplit("/", 1)[-1]
    h = hashlib.sha256()
    got = 0
    part = dst.with_name(dst.name + ".part")
    with part.open("wb") as f:
        while got < size:
            data = recv_frame(sock)
            f.write(data)
            h.update(data)
            got += len(data)
    r = recv_json(sock)
    if not r.get("ok") or r["sha256"] != h.hexdigest():
        part.unlink(missing_ok=True)
        print("Prüfsumme stimmt nicht!", file=sys.stderr)
        return 1
    part.replace(dst)
    print(f"{size} Bytes -> {dst} (sha256 ok)")
    return 0


SERVER_DIR = Path(__file__).resolve().parent.parent / "server"


def default_server_exe():
    """Gebaute xpserver.exe: server/dist (build.sh) oder server/bin/Release/net35 (dotnet build)."""
    for p in (SERVER_DIR / "dist" / "xpserver.exe", SERVER_DIR / "bin" / "Release" / "net35" / "xpserver.exe"):
        if p.exists():
            return p
    return None


def is_net(ping):
    return ping.get("kind") == "net"


def wait_for_server(host, port, want_sha=None, want_net=None, seconds=40):
    """Wartet, bis der Server wieder antwortet. Liefert die ping-Antwort oder None."""
    key = load_key()
    deadline = time.time() + seconds
    time.sleep(1.5)
    while time.time() < deadline:
        try:
            s2 = connect(host, port, key)
        except (OSError, SystemExit):
            time.sleep(1)
            continue
        send_json(s2, {"op": "ping"})
        after = recv_json(s2)
        send_json(s2, {"op": "bye"})
        s2.close()
        if want_net is not None and is_net(after) != want_net:
            time.sleep(1)
            continue
        return after
    return None


def cmd_update(sock, args):
    send_json(sock, {"op": "ping"})
    before = recv_json(sock)
    if "sha256" not in before:
        print("Der Server auf dem XP ist v1 und kennt kein update. Einmal von Hand "
              "xpserver.py + xpserver.bat auf den XP-Rechner kopieren und neu starten.",
              file=sys.stderr)
        return 1
    if args.file:
        src = Path(args.file)
    elif is_net(before):
        src = default_server_exe()
        if src is None:
            print("Keine gebaute xpserver.exe gefunden. Erst bauen: cd server && ./build.sh", file=sys.stderr)
            return 1
    else:
        src = SERVER_DIR / "legacy-python" / "xpserver.py"
        print("Hinweis: auf dem XP läuft noch der alte Python-Server. Umstieg auf den .NET-Server: xpctl.py migrate",
              file=sys.stderr)
    if is_net(before) and src.suffix.lower() != ".exe":
        print(f"Der Server ist die .NET-Version, {src.name} passt nicht (xpserver.exe schicken).", file=sys.stderr)
        return 1
    if not is_net(before) and src.suffix.lower() == ".exe":
        print("Der Python-Server kann sich nicht durch eine Exe ersetzen. Dafür gibt es: xpctl.py migrate", file=sys.stderr)
        return 1
    data = src.read_bytes()
    want = hashlib.sha256(data).hexdigest()
    if before["sha256"] == want and not args.force:
        print(f"Server ist schon auf Stand (v{before['version']}, sha256 {want[:12]}), nichts zu tun.")
        return 0
    send_json(sock, {"op": "update", "size": len(data), "sha256": want})
    r = recv_json(sock)
    if not r.get("ready"):
        print("Fehler:", r.get("error"), file=sys.stderr)
        return 1
    for i in range(0, len(data), CHUNK):
        send_frame(sock, data[i:i + CHUNK])
    r = recv_json(sock)
    if not r.get("ok"):
        print("Fehler:", r.get("error"), file=sys.stderr)
        return 1
    print(f"eingespielt (v{before['version']} -> {src.name}), warte auf Neustart ...")
    sock.close()
    after = wait_for_server(args.host, args.port)
    if after is None:
        print("Server nach 40 s nicht wieder erreichbar, siehe xpserver.log auf dem XP.", file=sys.stderr)
        return 1
    if after["sha256"] == want:
        print(f"neu gestartet, läuft v{after['version']} (sha256 {want[:12]})")
        return 0
    print(f"Server läuft, aber mit alter/anderer Version (v{after['version']}): "
          "vermutlich Rollback nach Startfehler, siehe xpserver.log auf dem XP.", file=sys.stderr)
    return 1


def _put_bytes(sock, data, path):
    send_json(sock, {"op": "put", "path": path, "size": len(data), "name": path.rsplit("\\", 1)[-1]})
    r = recv_json(sock)
    if not r.get("ready"):
        raise RuntimeError(f"put {path}: {r.get('error')}")
    for i in range(0, len(data), CHUNK):
        send_frame(sock, data[i:i + CHUNK])
    r = recv_json(sock)
    if not r.get("ok") or r.get("sha256") != hashlib.sha256(data).hexdigest():
        raise RuntimeError(f"put {path}: {r.get('error') or 'Prüfsumme falsch'}")


def _run(sock, cmd, timeout=60):
    r = rpc(sock, {"op": "run", "cmd": cmd, "timeout": timeout})
    if r is None:
        raise RuntimeError(f"run fehlgeschlagen: {cmd}")
    return r


def cmd_migrate(sock, args):
    """Alter Python-Server -> .NET-Server (xpserver.exe) auf demselben Rechner und Port."""
    send_json(sock, {"op": "ping"})
    before = recv_json(sock)
    if is_net(before):
        print(f"Auf {args.host} läuft schon der .NET-Server (v{before['version']}). Für neue Versionen: xpctl.py update")
        return 0
    src = Path(args.file) if args.file else default_server_exe()
    if src is None or not src.exists():
        print("Keine gebaute xpserver.exe gefunden. Erst bauen: cd server && ./build.sh", file=sys.stderr)
        return 1
    folder = before["script"].rsplit("\\", 1)[0]
    exe = folder + "\\xpserver.exe"
    print(f"Python-Server v{before['version']} in {folder}")

    # Prozesse: python.exe mit xpserver.py und die cmd.exe der xpserver.bat-Schleife darüber
    r = rpc(sock, {"op": "ps", "filter": "python", "cmdline": True})
    py = [p for p in (r or {}).get("procs", []) if "xpserver.py" in (p.get("cmdline") or "")]
    if len(py) != 1:
        print(f"python.exe mit xpserver.py nicht eindeutig gefunden ({len(py)} Treffer), Abbruch.", file=sys.stderr)
        return 1
    pypid = py[0]["pid"]
    out = _run(sock, f"wmic process where ProcessId={pypid} get ParentProcessId /value")["stdout"]
    m = re.search(r"ParentProcessId=(\d+)", out)
    cmdpid = int(m.group(1)) if m else 0
    if cmdpid:
        out = _run(sock, f"wmic process where ProcessId={cmdpid} get CommandLine /value")["stdout"]
        if "xpserver.bat" not in out.lower():
            cmdpid = 0
    print(f"  python.exe PID {pypid}" + (f", xpserver.bat-Schleife PID {cmdpid}" if cmdpid else ", ohne .bat-Schleife"))

    # Dateien
    _put_bytes(sock, src.read_bytes(), exe)
    print(f"  {src.name} -> {exe}")
    has = _run(sock, f'if exist "{folder}\\xpserver.json" (echo json) & if exist "{folder}\\xpctl.key" (echo key)')["stdout"]
    if "json" not in has:
        ip = sock.getsockname()[0]
        net = args.allow or ".".join(ip.split(".")[:3]) + ".0/24"
        cfg = json.dumps({"allow": [net]}, indent=2) + "\n"
        _put_bytes(sock, cfg.encode("utf-8"), folder + "\\xpserver.json")
        print(f"  xpserver.json angelegt, erlaubt: {net}")
    if "key" not in has:
        _put_bytes(sock, load_key(), folder + "\\xpctl.key")
        print("  xpctl.key angelegt (Passwort einmalig unverschlüsselt im LAN übertragen)")

    # Firewall und Autostart
    fw = _run(sock, f'netsh firewall add allowedprogram program="{exe}" name=xpserver mode=ENABLE scope=SUBNET')
    print("  Firewall-Freigabe: " + (fw["stdout"].strip() or fw["stderr"].strip() or "?"))
    if not args.no_autostart:
        moved = []
        for hive, name in (("HKCU", "Startup"), ("HKLM", "Common Startup")):
            rr = rpc(sock, {"op": "reg", "key": f"{hive}\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Shell Folders",
                            "values": True, "recurse": 0})
            d = None
            for v in (rr or {}).get("tree", {}).get("values", []):
                if v.get("name") == name:
                    d = v.get("data")
            if not d:
                continue
            lst = rpc(sock, {"op": "ls", "path": d, "files_only": True})
            for e in (lst or {}).get("entries", []):
                pth = e.get("path", "")
                if not pth.lower().endswith((".bat", ".cmd")):
                    continue
                c = rpc(sock, {"op": "cat", "path": pth})
                if c and "xpserver.bat" in "\n".join(c.get("lines") or []).lower():
                    # Aus dem Autostart-Ordner heraus (dort würde Explorer jede Datei beim Login öffnen)
                    dst = folder + "\\autostart-alt-" + pth.rsplit("\\", 1)[-1] + ".txt"
                    _run(sock, f'move /y "{pth}" "{dst}"')
                    moved.append((pth, dst))
        _run(sock, f'reg add HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run /v xpserver /t REG_SZ /d "\\"{exe}\\"" /f')
        for a_, b_ in moved:
            print(f"  alter Autostart entfernt: {a_} -> {b_}")
        print("  Autostart: HKCU\\...\\Run\\xpserver -> xpserver.exe")

    # Umschalten: Hilfs-Batch beendet .bat-Schleife und Python und startet die Exe
    bat = "\r\n".join([
        "@echo off",
        "ping -n 3 127.0.0.1 >nul",
        f"taskkill /F /PID {cmdpid} >nul 2>&1" if cmdpid else "rem keine .bat-Schleife",
        f"taskkill /F /PID {pypid} >nul 2>&1",
        "ping -n 2 127.0.0.1 >nul",
        f'start "" /d "{folder}" "{exe}"',
        'del "%~f0"',
    ]) + "\r\n"
    helper = folder + "\\xpctl-migrate.bat"
    _put_bytes(sock, bat.encode("ascii"), helper)
    rpc(sock, {"op": "run", "cmd": f'"{helper}"', "detach": True})
    print("Umschalten ... (Python-Server wird beendet, xpserver.exe startet)")
    sock.close()
    after = wait_for_server(args.host, args.port, want_net=True, seconds=60)
    if after is None:
        print("Der .NET-Server meldet sich nicht. Auf dem XP nachsehen (xpserver.log, Symbol im Infobereich). "
              "Zurück zum alten Server: xpserver.bat starten.", file=sys.stderr)
        return 1
    print(f"fertig: .NET-Server v{after['version']} läuft ({after.get('runtime', '')}). "
          "Die alten Dateien (xpserver.py, xpserver.bat) können weg, wenn alles passt.")
    return 0


def rpc(sock, req):
    send_json(sock, req)
    r = recv_json(sock)
    if not r.get("ok"):
        print("Fehler:", r.get("error"), file=sys.stderr)
        return None
    return r


def one(s, n=90):
    s = " ".join(str(s).split())
    return s if len(s) <= n else s[:n - 1] + "…"


def center(r):
    return (r[0] + r[2]) // 2, (r[1] + r[3]) // 2


INTERACTIVE = {"button", "edit", "combobox", "comboboxex32", "listbox", "syslistview32",
               "systreeview32", "systabcontrol32", "richedit20a", "richedit20w", "richedit50w",
               "msctls_trackbar32", "msctls_updown32", "scrollbar"}
HIDE_TOP = {"progman", "shell_traywnd", "consolewindowclass"}


def cmd_ui(sock, args):
    req = {"op": "ui"}
    if args.hwnd:
        req["hwnd"] = int(args.hwnd, 0)
    if args.all:
        req["all"] = True
    r = rpc(sock, req)
    if r is None:
        return 1
    if args.json:
        print(json.dumps(r, ensure_ascii=False))
        return 0
    print(f"Bildschirm {r['screen'][0]}x{r['screen'][1]}, Vordergrund {r['foreground']:#x}. "
          "Koordinaten = Bildschirm, Klick-Mitte steht bei jedem Eintrag als @x,y")
    print("Fenster (vorne zuerst):")
    n = 0
    for w in r["windows"]:
        if not args.all and w["class"].lower() in HIDE_TOP:
            continue
        n += 1
        if n > 15 and not args.all:
            print("  ... (weitere mit --all)")
            break
        flags = ("*" if w["hwnd"] == r["foreground"] else " ")
        extra = []
        if w["hung"]:
            extra.append("HÄNGT")
        if w["minimized"]:
            extra.append("minimiert")
        if w["owner"]:
            extra.append(f"Owner {w['owner']:#x}")
        if not w["enabled"]:
            extra.append("gesperrt")
        l, t, rr, b = w["rect"]
        print(f"{flags}{w['hwnd']:#x} \"{one(w['text'], 70)}\" {w['exe']} pid={w['pid']} "
              f"{l},{t}-{rr},{b} {' '.join(extra)}".rstrip())
    tw = next((w for w in r["windows"] if w["hwnd"] == r["target"]), None)
    title = f" \"{one(tw['text'], 50)}\"" if tw else ""
    print(f"Controls von {r['target']:#x}{title}:")
    cnt = 0
    for c in r["controls"]:
        interactive = c["class"].lower() in INTERACTIVE
        if not args.all and not c["text"] and not interactive:
            continue
        cnt += 1
        l, t, rr, b = c["rect"]
        cx, cy = center(c["rect"])
        flags = []
        if not c["enabled"]:
            flags.append("aus")
        if c.get("checked") is not None:
            flags.append("an" if c["checked"] == 1 else "teil" if c["checked"] == 2 else "leer")
        print(f"  {c['hwnd']:#x} {c['class']} \"{one(c['text'])}\" @{cx},{cy} {rr - l}x{b - t}"
              f"{' [' + ','.join(flags) + ']' if flags else ''}")
    if cnt == 0:
        print("  (keine Controls, vermutlich selbstgezeichnetes Fenster: shot benutzen)")
    return 0


def _grep(rows, text, key):
    if not text:
        return rows
    t = text.lower()
    return [r for r in rows if t in key(r).lower()]


def cmd_updates(sock, args):
    r = rpc(sock, {"op": "updates"})
    if r is None:
        return 1
    ups = _grep(r["updates"], args.grep,
                lambda u: " ".join([u["kb"], u["title"], u["product"], u["category"], u["date"]]))
    if args.json:
        print(json.dumps({"state": r["state"], "updates": ups}, ensure_ascii=False, indent=1))
        return 0
    st = r["state"]
    sp = st["csd"] or "kein Service Pack"
    print(f"{st['product']} {st['version']}.{st['build']}, {sp}")
    if st["posready_key"]:
        print(f"POSReady-Hack: ja (HKLM\\SYSTEM\\WPA\\PosReady vorhanden, Werte {st['posready']})")
    else:
        print("POSReady-Hack: nein (HKLM\\SYSTEM\\WPA\\PosReady fehlt)")
    for u in ups:
        print(f"{u['kb'] or '-':<9} {u['date'] or '?':<10}  {u['category']:<15} "
              f"{one(u['product'], 28):<28}  {one(u['title'], 70)}")
    suffix = f" (Filter: {args.grep})" if args.grep else ""
    print(f"Summe: {len(ups)} Updates{suffix}")
    return 0


def cmd_programs(sock, args):
    r = rpc(sock, {"op": "programs"})
    if r is None:
        return 1
    rows = r["programs"]
    hidden = sum(1 for p in rows if p["kind"])
    if not args.all:
        rows = [p for p in rows if not p["kind"]]
    rows = _grep(rows, args.grep, lambda p: " ".join([p["name"], p["publisher"], p["version"]]))
    if args.json:
        print(json.dumps(rows, ensure_ascii=False, indent=1))
        return 0
    for p in rows:
        tag = f" [{p['kind']}]" if p["kind"] else ""
        print(f"{one(p['name'], 60):<60} {one(p['version'], 18):<18} {p['date'] or '':<10} "
              f"{one(p['publisher'], 30)}{tag}")
    extra = "" if args.all else f", {hidden} Updates/Systemkomponenten ausgeblendet (--all)"
    print(f"Summe: {len(rows)} Programme{extra}")
    return 0


def _mb(n):
    return f"{n / 1024:.1f} GB" if n >= 1024 else f"{n} MB"


TEMP_SOURCES = ["acpi", "smart", "nvidia", "hwinfo", "speedfan"]


def cmd_temps(sock, args):
    """Temperaturen aller Quellen, die der Rechner hergibt. Quellen ohne Sensor erscheinen als Hinweis."""
    req = {"op": "temps"}
    if args.source:
        req["sources"] = [s.strip() for x in args.source for s in x.split(",") if s.strip()]
    r = rpc(sock, req)
    if r is None:
        return 1
    if args.json:
        print(json.dumps(r, ensure_ascii=False, indent=1))
        return 0
    sens = r["sensors"]
    if sens:
        wn = max(len(s["name"]) for s in sens)
        ws = max(len(s["source"]) for s in sens)
        for s in sens:
            c, warn, crit = s["celsius"], s.get("warn"), s.get("crit")
            lim = []
            if warn is not None:
                lim.append(f"drosselt ab {warn:g}")
            if crit is not None:
                lim.append(f"kritisch {crit:g}")
            mark = "  KRITISCH" if crit is not None and c >= crit else "  warm" if warn is not None and c >= warn else ""
            extra = f"  ({', '.join(lim)})" if lim else ""
            print(f"{s['source']:<{ws}}  {s['name']:<{wn}}  {c:5.1f} °C{mark}{extra}")
    else:
        print("keine Temperatur gefunden")
    for st in r["sources"]:
        msg = st.get("note") or st.get("error")
        if msg:
            print(f"  {st['name']}: {msg}")
    return 0


def _savegames():
    """client/savegames erst bei Bedarf laden: eine einzeln kopierte xpctl.py läuft auch ohne."""
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    try:
        import savegames
    except ImportError:
        sys.exit("saves braucht den Ordner savegames/ neben xpctl.py (aus dem Repo, client/savegames)")
    globals()["savegames"] = savegames
    return savegames


class XpRemote:
    """Dateizugriff für client/savegames über xpserver (eine Verbindung, mehrere Ops).
    Erfüllt die Schnittstelle savegames.Remote."""

    def __init__(self, sock):
        self.sock = sock

    def _call(self, req):
        send_json(self.sock, req)
        return recv_json(self.sock)

    def read(self, path):
        send_json(self.sock, {"op": "get", "path": path})
        r = recv_json(self.sock)
        if not r.get("ready"):
            raise savegames.SaveError(f"{path}: {r.get('error')}")
        buf = bytearray()
        while len(buf) < r["size"]:
            buf += recv_frame(self.sock)
        end = recv_json(self.sock)
        if not end.get("ok") or end["sha256"] != hashlib.sha256(buf).hexdigest():
            raise savegames.SaveError(f"{path}: Prüfsumme der Übertragung stimmt nicht")
        return bytes(buf)

    def write(self, path, data, mkdir=False):
        send_json(self.sock, {"op": "put", "path": path, "size": len(data),
                              "name": path.rsplit("\\", 1)[-1], "mkdir": mkdir})
        r = recv_json(self.sock)
        if not r.get("ready"):
            raise savegames.SaveError(f"{path}: {r.get('error')}")
        for i in range(0, len(data), CHUNK):
            send_frame(self.sock, data[i:i + CHUNK])
        r = recv_json(self.sock)
        if not r.get("ok") or r.get("sha256") != hashlib.sha256(data).hexdigest():
            raise savegames.SaveError(f"{path}: {r.get('error') or 'Prüfsumme der Übertragung falsch'}")

    def files(self, folder):
        r = self._call({"op": "ls", "path": folder, "files_only": True})
        if not r.get("ok"):
            return []
        return [savegames.Entry(e["path"].rsplit("\\", 1)[-1], e.get("size", 0), e.get("mtime", ""))
                for e in r["entries"] if not e.get("dir") and not e.get("error")]

    def md5(self, path):
        r = self._call({"op": "hash", "paths": [path], "algo": "md5"})
        h = (r.get("hashes") or {}).get(path, "") if r.get("ok") else ""
        return h if re.fullmatch(r"[0-9a-f]{32}", h or "") else None

    def running(self, exe_name):
        r = self._call({"op": "ps", "filter": exe_name})
        if not r.get("ok"):
            raise savegames.SaveError(f"Prozessliste nicht lesbar ({r.get('error')}), schreibe vorsichtshalber nicht")
        return any(p["name"].lower() == exe_name.lower() for p in r.get("procs", []))

    def reg(self, key, name):
        r = self._call({"op": "reg", "key": key, "values": True, "recurse": 0})
        if not r.get("ok"):
            return None
        for v in r["tree"].get("values", []):
            if v.get("name", "").lower() == name.lower():
                return str(v.get("data"))
        return None


SAVES_BACKUP = r"C:\xpctl\backup\saves"


def _saves_overview(rem, as_json):
    rows = []
    for fam in savegames.games():
        g = savegames.resolve(rem, fam)
        row = {"id": fam.id, "title": fam.title, "installed": g is not None}
        if g is not None:
            row.update(install_dir=g.install_dir, version=g.version_text, writable=g.writable,
                       profiles=g.profiles())
        rows.append(row)
    if as_json:
        print(json.dumps(rows, ensure_ascii=False, indent=1))
        return 0
    for r in rows:
        if not r["installed"]:
            print(f"{r['id']:<8} {r['title']}: nicht installiert")
            continue
        print(f"{r['id']:<8} {r['title']}  [{r['version']}]")
        print(f"         {len(r['profiles'])} Profile: {', '.join(r['profiles']) or '-'}")
    print("\nDetails: xpctl.py saves SPIEL [PROFIL]   Formate: xpctl.py saves formats")
    return 0


def _saves_formats():
    for fam in savegames.games():
        print(f"{fam.id:<8} {fam.title}  (Klasse {fam.__name__}, ohne bekannte Exe nur lesen)")
        for v in savegames.versions(fam):
            print(f"         {v.__name__}: {v.version}, md5 {', '.join(sorted(v.exe_md5))}")
    return 0


def _field_json(f, x):
    return {"file": f.name, "key": f"{f.kind}.{x.key}", "label": x.label, "value": x.value,
            "shown": x.shown(), "writable": x.writable, "verified": x.verified, "note": x.note}


def _print_profile(g, p):
    print(f"{g.title}, Profil {p.name}  [{g.version_text}]")
    for f in p.files:
        if f.sensitive:
            print(f"\n  {f.name}: {f.label}")
            continue
        print(f"\n  {f.name} ({f.label}, {len(f.original)} Byte)")
        for x in f.fields():
            flags = ("schreibbar" if x.writable else "") + (", im Spiel geprüft" if x.verified and x.writable else "")
            note = f"  [{x.note}]" if x.note else ""
            print(f"    {f.kind + '.' + x.key:<22} {x.label:<28} {x.shown()}{note}" + (f"  ({flags})" if flags else ""))
    probs = p.problems()
    if probs:
        print("\n  Probleme:")
        for f, msg in probs:
            print(f"    {f.name}: {msg}")
        print("  Reparieren: xpctl.py saves " + g.id + " repair " + p.name)


def cmd_saves(sock, args):
    _savegames()
    savegames.Remote.register(XpRemote)
    rem = XpRemote(sock)
    try:
        if not args.game:
            return _saves_overview(rem, args.json)
        if args.game == "formats":
            return _saves_formats()
        fam = savegames.find_game(args.game)
        g = savegames.resolve(rem, fam)
        if g is None:
            print(f"{fam.title} ist auf diesem Rechner nicht installiert (oder die Registry kennt es nicht)")
            return 1
        action, prof = args.action, args.profile
        if action and action not in ("show", "set", "repair", "backup"):
            action, prof = "show", action              # saves nfsu Michael
        if not action:
            names = g.profiles()
            if args.json:
                print(json.dumps({"game": fam.id, "version": g.version_text, "writable": g.writable,
                                  "save_dir": g.save_dir(), "profiles": names}, ensure_ascii=False, indent=1))
                return 0
            print(f"{g.title}  [{g.version_text}]\n  Ordner: {g.save_dir()}")
            for n in names:
                p = g.load(n)
                probs = p.problems()
                state = "ok" if not probs else f"{len(probs)} Problem(e)"
                print(f"  {n:<16} {', '.join(f.name for f in p.files)}  [{state}]")
            for note in g.notes:
                print(f"\n  Hinweis: {note}")
            return 0
        if action == "backup":
            bdir, done = g.backup([prof] if prof else None, SAVES_BACKUP)
            print(f"{len(done)} Dateien gesichert nach {bdir}")
            return 0
        if not prof:
            print(f"Profil fehlt. Vorhanden: {', '.join(g.profiles())}", file=sys.stderr)
            return 2
        p = g.load(prof)
        if action == "show":
            if args.json:
                print(json.dumps({"game": fam.id, "version": g.version_text, "profile": p.name,
                                  "fields": [_field_json(f, x) for f, x in p.fields()],
                                  "files": [{"name": f.name, "kind": f.kind, "size": len(f.original),
                                             "sensitive": f.sensitive} for f in p.files],
                                  "problems": [f"{f.name}: {m}" for f, m in p.problems()]},
                                 ensure_ascii=False, indent=1))
            else:
                _print_profile(g, p)
            return 0
        if action == "set":
            if not args.assign:
                print("set braucht FELD=WERT", file=sys.stderr)
                return 2
            for a in args.assign:
                if "=" not in a:
                    print(f"{a}: FELD=WERT erwartet", file=sys.stderr)
                    return 2
                k, v = a.split("=", 1)
                before = p.find(k)[1].shown()
                p.set(k.strip(), v.strip())
                print(f"  {k}: {before} -> {p.find(k)[1].shown()}")
        else:  # repair
            fixed = [f.name for f in p.files if not f.sensitive and f.repair()]
            if not fixed:
                print("nichts zu reparieren")
                return 0
            print("  repariert: " + ", ".join(fixed) + " (dass das Spiel reparierte Binärdateien annimmt, ist nicht geprüft)")
        if args.dry_run:
            print("--dry-run: nichts geschrieben (" + ", ".join(f.name for f in p.dirty()) + ")")
            return 0
        bdir, written = g.store(p, SAVES_BACKUP, ignore_running=args.force)
        if not written:
            print("keine Änderung")
            return 0
        print(f"geschrieben und zurückgelesen: {', '.join(w.rsplit(chr(92), 1)[-1] for w in written)}")
        print(f"Sicherung der alten Dateien: {bdir}")
        return 0
    except savegames.SaveError as e:
        print(f"Fehler: {e}", file=sys.stderr)
        return 1


def cmd_sysinfo(sock, args):
    r = rpc(sock, {"op": "sysinfo"})
    if r is None:
        return 1
    if args.json:
        print(json.dumps(r, ensure_ascii=False, indent=1))
        return 0
    w, c = r["windows"], r["cpu"]
    print(f"Rechner:    {r['host']}")
    print(f"Windows:    {w['product']} {w['version']}.{w['build']} {w['sp']}".rstrip()
          + (", POSReady-Hack" if w["posready"] else ""))
    if w["installed"] or w["owner"]:
        print(f"Installiert: {w['installed']}  Besitzer: {w['owner']}")
    lg = r.get("language", {})
    if "error" not in lg:
        print(f"Sprache:    System {lg['system']} ({lg['system_lcid']}), Oberfläche {lg['ui']} ({lg['ui_lcid']})")
    mhz = f", {c['mhz']} MHz" if c["mhz"] else ""
    print(f"CPU:        {c['name']}{mhz}, {c['cores']} logische(r) Kern(e)")
    if "ram" in r:
        m = r["ram"]
        print(f"RAM:        {_mb(m['total_mb'])} gesamt, {_mb(m['free_mb'])} frei ({m['load_pct']} % belegt)")
    for d in r["drives"]:
        size = f"{_mb(d['free_mb'])} frei von {_mb(d['total_mb'])}" if "total_mb" in d else "kein Medium/nicht lesbar"
        print(f"Laufwerk:   {d['drive']:<4} {d['type']:<8} {size}")
    print(f"IE:         {r['ie'] or '?'}")
    dx = r["directx"]
    print(f"DirectX:    {dx['name'] or '?'} (Registry {dx['raw'] or '?'})")
    if r["dotnet"]:
        for n in r["dotnet"]:
            sp = f" SP{n['sp']}" if n.get("sp") else ""
            print(f".NET:       {n['name']} {n['version']}{sp}")
    else:
        print(".NET:       keins")
    return 0


def _reg_line(v):
    d = v["data"]
    if isinstance(d, list):
        d = " | ".join(str(x) for x in d)
    return f"{v['name'] or '(Standard)'} [{v['type']}] = {d}"


def _reg_print(n, indent, top):
    pad = "  " * indent
    name = n["path"].rsplit("\\", 1)[-1] if not top else n["path"]
    print(f"{pad}[{name or '(Wurzel)'}]")
    if "error" in n:
        print(f"{pad}  Fehler: {n['error']}")
        return
    for v in n["values"]:
        print(f"{pad}  {_reg_line(v)}")
    if not n["values"] and n.get("nvalues"):
        print(f"{pad}  ({n['nvalues']} Werte, mit --values anzeigen)")
    if "children" in n:
        for c in n["children"]:
            _reg_print(c, indent + 1, False)
        if n.get("truncated"):
            print(f"{pad}  ... abgeschnitten (zu viele Schlüssel)")
    elif n["subkeys"]:
        print(f"{pad}  Unterschlüssel: {', '.join(n['subkeys'])}")


def cmd_reg(sock, args):
    r = rpc(sock, {"op": "reg", "key": args.key, "values": args.values, "recurse": args.recurse})
    if r is None:
        return 1
    if args.json:
        print(json.dumps(r["tree"], ensure_ascii=False, indent=1))
        return 0
    _reg_print(r["tree"], 0, True)
    return 0


def cmd_settings(sock, args):
    req = {"op": "settings"}
    if args.only:
        req["only"] = [x.strip() for x in args.only.split(",")]
    r = rpc(sock, req)
    if r is None:
        return 1
    if args.json:
        print(json.dumps(r, ensure_ascii=False, indent=1))
        return 0
    print(f"Rechner {r['host']}, Server läuft als Benutzer {r['user']} (HKCU = dieser Benutzer)")
    for sec in r["sections"]:
        print(f"\n== {sec['title']} ({sec['id']}) ==")
        for it in sec["items"]:
            v = it["v"]
            v = "(nicht gesetzt)" if v is None else " | ".join(str(x) for x in v) if isinstance(v, list) else v
            note = f"  [{it['note']}]" if it.get("note") else ""
            path = f"   <- {it['reg']}" if args.paths and it.get("reg") else ""
            print(f"  {one(it['k'], 70)}: {one(v, 300)}{note}{path}")
    return 0


def cmd_productkey(sock, args):
    r = rpc(sock, {"op": "productkey"})
    if r is None:
        return 1
    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    fd = os.open(out, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w") as f:
        f.write(r["key"] + "\n")
    os.chmod(out, 0o600)
    parts = r["key"].split("-")
    ok = r["format_ok"] and len(parts) == 5 and all(len(x) == 5 for x in parts)
    print(f"Key gespeichert in {out}, Format {'ok (5x5)' if ok else 'NICHT ok'}")
    return 0 if ok else 1


def write_png(path: Path, w: int, h: int, stride: int, bgr: bytes) -> None:
    rows = []
    for y in range(h):
        row = bgr[y * stride:y * stride + w * 3]
        rgb = bytearray(len(row))
        rgb[0::3] = row[2::3]
        rgb[1::3] = row[1::3]
        rgb[2::3] = row[0::3]
        rows.append(b"\x00" + bytes(rgb))

    def chunk(t: bytes, d: bytes) -> bytes:
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
    png = (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(b"".join(rows), 9)) + chunk(b"IEND", b""))
    path.write_bytes(png)


def cmd_shot(sock, args):
    req = {"op": "shot", "scale": 1.0 if args.full else args.scale}
    if args.hwnd:
        req["hwnd"] = int(args.hwnd, 0)
    if args.region:
        req["region"] = [int(v) for v in args.region.split(",")]
    send_json(sock, req)
    r = recv_json(sock)
    if not r.get("ready"):
        print("Fehler:", r.get("error"), file=sys.stderr)
        return 1
    payload = bytearray()
    while len(payload) < r["size"]:
        payload += recv_frame(sock)
    fin = recv_json(sock)
    if not fin.get("ok") or fin["sha256"] != hashlib.sha256(payload).hexdigest():
        print("Prüfsumme stimmt nicht!", file=sys.stderr)
        return 1
    if args.out:
        out = Path(args.out)
    else:
        shots = Path(os.environ.get("XPCTL_SHOTS") or Path.home() / "xpctl-shots")
        out = shots / f"xp-{time.strftime('%Y%m%d-%H%M%S')}.png"
    out.parent.mkdir(parents=True, exist_ok=True)
    write_png(out, r["width"], r["height"], r["stride"], zlib.decompress(bytes(payload)))
    l, t, rr, b = r["rect"]
    s = r["scale"]
    print(f"{out}  ({r['width']}x{r['height']}, Bereich {l},{t}-{rr},{b}, Faktor {s:g}; "
          f"Bildpunkt (x,y) = Bildschirm ({l}+x/{s:g}, {t}+y/{s:g}))")
    return 0


def cmd_click(sock, args):
    req = {"op": "click", "x": args.x, "y": args.y,
           "button": "right" if args.right else "left", "double": args.double,
           "move_only": args.move}
    return 0 if rpc(sock, req) else 1


def cmd_press(sock, args):
    r = rpc(sock, {"op": "press", "hwnd": int(args.hwnd, 0)})
    if r:
        print(f"ok ({r['how']})")
    return 0 if r else 1


def cmd_settext(sock, args):
    return 0 if rpc(sock, {"op": "settext", "hwnd": int(args.hwnd, 0), "text": args.text}) else 1


def cmd_type(sock, args):
    return 0 if rpc(sock, {"op": "type", "text": args.text}) else 1


def cmd_key(sock, args):
    return 0 if rpc(sock, {"op": "key", "keys": args.keys}) else 1


def cmd_focus(sock, args):
    r = rpc(sock, {"op": "focus", "hwnd": int(args.hwnd, 0)})
    if r:
        print("im Vordergrund" if r["foreground"] else "nicht im Vordergrund (XP-Fokussperre), ggf. per click")
    return 0 if r else 1


def cmd_close(sock, args):
    return 0 if rpc(sock, {"op": "close", "hwnd": int(args.hwnd, 0)}) else 1


def tile_rects(n, cols, sw, sh, top=0, bottom=0, gap=0):
    """Gitter für n Fenster: Liste (x, y, w, h), Zeilen von oben, gleich große Zellen."""
    if n <= 0:
        return []
    cols = max(1, min(cols or math.ceil(math.sqrt(n)), n))
    rows = math.ceil(n / cols)
    w = (sw - gap * (cols + 1)) // cols
    h = (sh - top - bottom - gap * (rows + 1)) // rows
    return [(gap + (i % cols) * (w + gap), top + gap + (i // cols) * (h + gap), w, h) for i in range(n)]


def cmd_win(sock, args):
    if args.action == "move":
        r = rpc(sock, {"op": "winmove", "hwnd": int(args.hwnd, 0), "x": args.x, "y": args.y, "w": args.w, "h": args.h})
        if r:
            print("{:#x} -> {},{}-{},{}".format(int(args.hwnd, 0), *r["rect"]))
        return 0 if r else 1
    u = rpc(sock, {"op": "ui"})
    if u is None:
        return 1
    rx = re.compile(args.title, re.I)
    wins = [w for w in u["windows"] if rx.search(w["text"]) and (not args.exe or w["exe"].lower() == args.exe.lower())]
    if not wins:
        print(f"kein Fenster passt auf {args.title!r}")
        return 1
    wins.sort(key=lambda w: w["text"].lower())
    sw, sh = u["screen"]
    rc = 0
    for w, (x, y, ww, hh) in zip(wins, tile_rects(len(wins), args.cols, sw, sh, bottom=args.taskbar, gap=args.gap)):
        r = rpc(sock, {"op": "winmove", "hwnd": w["hwnd"], "x": x, "y": y, "w": ww, "h": hh})
        print(f"{w['hwnd']:#x} \"{one(w['text'], 50)}\" -> {x},{y} {ww}x{hh}" if r else f"{w['hwnd']:#x}: Fehler")
        rc |= 0 if r else 1
    return rc


BUTTON_CLASSES = ("button", "tnewbutton", "tbutton")

GROUPS_FILE = Path(os.environ["XPCTL_GROUPS"]) if os.environ.get("XPCTL_GROUPS") else None


def _lnk_print(links):
    for l in links:
        print(l["path"])
        print(f"  Ziel:    {l['target']}")
        if l["args"]:
            print(f"  Args:    {l['args']}")
        print(f"  Ordner:  {l['dir']}")
        print(f"  Icon:    {l['icon']}" + (f"  (angezeigt: {l['icon_used']})" if l["icon"].startswith(",") else ""))
        print(f"  Fenster: {ws_name(l['show'])}")


def ws_name(n):
    return {1: "normal", 3: "maximiert", 7: "minimiert"}.get(n, str(n))


def _group_after(sock, args):
    if getattr(args, "no_group", False) or GROUPS_FILE is None:
        return 0
    r = rpc(sock, {"op": "desk", "action": "group", "map": GROUPS_FILE.read_text(encoding="utf-8")})
    if r is None:
        return 1
    for m in r["messages"]:
        if "->" in m or m.startswith("ohne Gruppe"):
            print(f"  {m}")
    return 0


def cmd_lnk(sock, args):
    a = args.action
    if a == "show":
        req = {"op": "lnk", "action": "show", "names": args.names}
    elif a == "make":
        req = {"op": "lnk", "action": "make", "name": args.name, "target": args.target,
               "args": args.args, "dir": args.dir, "icon": args.icon, "folder": args.folder}
    elif a == "launcher":
        req = {"op": "lnk", "action": "launcher", "name": args.name, "image": args.image,
               "dir": args.dir, "exe": args.exe, "icon": args.icon, "folder": args.folder}
    elif a == "icon":
        req = {"op": "lnk", "action": "icon", "name": args.name, "icon": args.icon}
    else:
        req = {"op": "lnk", "action": "style", "name": args.name,
               "show": {"normal": 1, "max": 3, "min": 7}[args.show]}
    r = rpc(sock, req)
    if r is None:
        return 1
    _lnk_print(r["links"])
    if a in ("make", "launcher") and not args.folder:
        return _group_after(sock, args)
    return 0


def cmd_desk(sock, args):
    a = args.action
    req = {"op": "desk", "action": a}
    if a == "list":
        req["filter"] = args.filter
    elif a == "move":
        req.update(name=args.name, folder=args.folder)
    elif a == "foldericon":
        req.update(folder=args.folder, icon=args.icon)
    elif a == "group":
        mf = Path(args.map) if args.map else GROUPS_FILE
        if mf is None:
            sys.exit("desk group: nicht konfiguriert, --map DATEI oder XPCTL_GROUPS setzen")
        req["map"] = mf.read_text(encoding="utf-8")
    r = rpc(sock, req)
    if r is None:
        return 1
    if a == "list":
        for l in r["links"]:
            bad = "" if l["icon_ok"] else "  [Icon-Datei fehlt]"
            print(f"{l['folder']} | {l['name']} | {l['icon']}{bad}")
        print(f"Summe: {len(r['links'])}")
    else:
        for m in r["messages"]:
            print(m)
    return 0


def cmd_deskpos(sock, args):
    a = args.action
    req = {"op": "deskpos", "action": a}
    if a == "move":
        req.update(name=args.name, x=args.x, y=args.y)
    elif a == "grid":
        req.update(name=args.name, col=args.col, row=args.row)
    elif a == "row":
        req.update(row=args.row, rest=args.rest, names=args.names)
        if not args.rest and not args.names:
            sys.exit("row braucht Namen oder --rest")
    r = rpc(sock, req)
    if r is None:
        return 1
    if r.get("warning"):
        print("WARNUNG:", r["warning"])
    for i in r["icons"]:
        print(f"{one(i['name'], 40):<40} x={i['x']:>4} y={i['y']:>4}  Spalte {i['col']} Zeile {i['row']}")
    print(f"Raster {r['grid'][0]}x{r['grid'][1]}, Desktop {r['work'][0]}x{r['work'][1]}")
    return 0


def cmd_mount(sock, args):
    r = rpc(sock, {"op": "mount", "image": args.image, "drive": args.drive, "force": args.force})
    if r is None:
        return 1
    print(f"{r['drive']} vorher: {r['before'] or '(leer)'}, jetzt: {r['label'] or '(leer)'}")
    return 0


def cmd_unmount(sock, args):
    r = rpc(sock, {"op": "mount", "image": None, "drive": args.drive, "force": args.force})
    if r is None:
        return 1
    print(f"{r['drive']} ausgehängt (vorher: {r['before'] or '(leer)'})")
    return 0


def cmd_eject(sock, args):
    return 0 if rpc(sock, {"op": "eject", "force": args.force, "drive": args.drive}) else 1


def cmd_vol(sock, args):
    r = rpc(sock, {"op": "vol", "force": args.force, "drives": args.drives or None})
    if r is None:
        return 1
    for d, l in sorted(r["drives"].items()):
        print(f"{d} {l if l is not None else '(kein Medium)'}")
    return 0


def cmd_ripiso(sock, args):
    r = rpc(sock, {"op": "ripiso", "force": args.force, "drive": args.drive, "target": args.target})
    if r is None:
        return 1
    print(f"{r['target']}: Label {r['label']}, {r['sectors']} Sektoren, md5 {r['md5']}, {r['seconds']} s")
    if r["padded"]:
        print(f"Auslauf unlesbar, mit Nullen gefüllt: Sektor {r['padded'][0]} bis {r['padded'][1]} ({r['padded'][2]})")
    return 0


def cmd_pesec(sock, args):
    r = rpc(sock, {"op": "pesec", "paths": args.paths})
    if r is None:
        return 1
    for p, s in r["sections"].items():
        print(f"{p}: {s}")
    return 0


def cmd_driver(sock, args):
    if args.action == "list":
        r = rpc(sock, {"op": "driver", "action": "list", "filter": args.target})
        if r is None:
            return 1
        for d in r["devices"]:
            state = "läuft" if d["started"] else f"Problem {d['problem']}"
            print(f"{d['desc'] or '?'} | {state} | Dienst {d['service'] or '-'} | {d['ids'][0] if d['ids'] else '-'}")
        print(f"{len(r['devices'])} Geräte")
        return 0
    if not args.target:
        sys.exit("driver install braucht den INF-Pfad auf dem XP")
    r = rpc(sock, {"op": "driver", "action": "install", "inf": args.target, "hwid": args.hwid})
    if r is None:
        return 1
    for d in r["devices"]:
        print(f"{d['hwid']}: {'ok' if d['ok'] else 'Fehler %#x' % d['error']}{' (Neustart nötig)' if d['reboot'] else ''}")
    if r.get("oeminf"):
        print(f"kein Gerät angeschlossen, INF im Treiberspeicher: {r['oeminf']['path']}")
    return 0


def _norm(s):
    import unicodedata
    s = unicodedata.normalize("NFKD", s).encode("ascii", "ignore").decode().lower()
    return re.sub(r"[^a-z0-9]", "", s.replace("&", "and"))


def gog_index(games):
    """GOG-Spiele: Version aus dem Setup-Dateinamen auf einem SSH-Host
    (<Ordner>/<slug>/setup_<slug>_<ver>_(<build>).exe)."""
    import shlex
    import subprocess
    host = os.environ.get("XPCTL_GOG_SSH")
    base = os.environ.get("XPCTL_GOG_DIR", "").rstrip("/")
    if not host or not base:
        sys.exit("games --gog-ssh braucht XPCTL_GOG_SSH (SSH-Host) und XPCTL_GOG_DIR (Ordner mit <slug>/setup_*.exe)")
    out = subprocess.run(["ssh", host, "bash -c " + shlex.quote("ls -1 " + shlex.quote(base) + "/*/setup_*.exe")],
                         capture_output=True, text=True).stdout
    setups = {}
    for line in out.splitlines():
        m = re.match(re.escape(base) + r"/([^/]+)/setup_(.+?)_(\d[^_]*|v\d[^_]*)_\((?:[^)]*)\)\.exe$", line)
        if m:
            setups.setdefault(m.group(1), m.group(3))
    by_norm = {_norm(k.replace("_", "")): (k, v) for k, v in setups.items()}
    for g in games:
        info = next((v for k, v in (g.get("markers") or {}).items() if isinstance(v, dict) and v.get("gameId")), None)
        if not info:
            continue
        for cand in (info.get("name") or "", g["name"]):
            hit = by_norm.get(_norm(cand))
            if hit:
                g["gog_slug"], g["gog_setup"] = hit
                break
        g["gog_id"] = info.get("gameId")


def game_version(g):
    """Beste Versionsangabe aus den Rohdaten: GOG-Info, Uninstall, Exe-Strings, feste Exe-Version."""
    if g.get("gog_setup"):
        return f"GOG {g['gog_setup']}", "GOG-Setup"
    for rel, val in (g.get("markers") or {}).items():
        if rel.lower().endswith("gameinfo") and isinstance(val, str):
            lines = [l.strip() for l in val.splitlines() if l.strip()]
            if len(lines) > 1:
                return f"GOG {lines[1]}", "gameinfo"
    for u in g.get("uninstall") or []:
        v = u.get("version") or ""
        if v and not re.fullmatch(r"2\.[01]\.0\.\d+", v):   # 2.0.0.x/2.1.0.x = alte GOG-Installer-Version
            return v, "uninstall"
    ei = g.get("exe_info") or {}
    st = ei.get("strings") or {}
    for k in ("ProductVersion", "FileVersion"):
        if st.get(k):
            return st[k], f"exe {k}"
    if ei.get("file_version") and ei["file_version"] != "0.0.0.0":
        return ei["file_version"], "exe fest"
    return "", ""


def load_db(path):
    p = Path(path) if path else Path(__file__).resolve().with_name("game-versions.json")
    if not p.exists():
        if path:
            sys.exit(f"Datenbank nicht gefunden: {p}")
        return []
    data = json.loads(p.read_text(encoding="utf-8"))
    return data["games"] if isinstance(data, dict) else data


def db_lookup(db, g):
    """Eintrag zur Exe: erst md5, sonst Exe-Name + Größe. Teilen sich mehrere Spiele dieselbe Exe
    (z. B. DOSBox), entscheidet der Spielname; bleibt es mehrdeutig, gibt es keinen Treffer."""
    ei = g.get("exe_info") or {}
    if not ei or ei.get("missing"):
        return None
    exe = g["exe"].rsplit("\\", 1)[-1].lower()
    hits = [e for e in db if ei.get("md5") and e.get("md5") == ei["md5"]]
    if not hits:
        hits = [e for e in db if e.get("exe", "").lower() == exe and e.get("size") == ei.get("size")]
    if len(hits) > 1:
        want = _norm(g["name"])
        named = [e for e in hits if _norm(e.get("game", "")) == want]
        hits = named or [e for e in hits if want and (want in _norm(e.get("game", "")) or _norm(e.get("game", "")) in want)]
    return hits[0] if len(hits) == 1 else None


def db_text(e):
    cur = e.get("version", "")
    latest = e.get("latest", "")
    if e.get("status"):
        same = e["status"] != "patch"
        return f"bekannt: {cur}" + (f" ({e['status']})" if same else f", neuester: {latest}"), not same
    same = latest.lower().startswith("gleich") or _core_version(cur) == _core_version(latest)
    return f"bekannt: {cur}" + (" (aktuell)" if same else f", neuester: {latest}"), not same


def _core_version(v):
    return re.sub(r"\s*\(.*?\)", "", v or "").strip().lower()


def cmd_games(sock, args):
    r = rpc(sock, {"op": "games", "filter": args.filter})
    if r is None:
        return 1
    games = r["games"]
    if args.gog_ssh:
        gog_index(games)
    db = load_db(args.db)
    for g in games:
        g["version_guess"], g["version_source"] = game_version(g) if not g.get("skip") else ("", "")
        hit = db_lookup(db, g) if db and not g.get("skip") else None
        if hit:
            g["db"] = {k: hit.get(k) for k in ("game", "version", "latest", "confidence", "status")}
    if args.json:
        Path(args.json).write_text(json.dumps(games, ensure_ascii=False, indent=1), encoding="utf-8")
        print(f"{len(games)} Verknüpfungen nach {args.json}")
        return 0
    for g in games:
        if g.get("error"):
            print(f"{g['name']}: Fehler {g['error']}")
            continue
        if g.get("skip"):
            continue
        ei = g.get("exe_info") or {}
        exe = g["exe"].rsplit("\\", 1)[-1]
        miss = " [Exe fehlt]" if ei.get("missing") else ""
        print(f"{g['name']} | {g['version_guess'] or '?'} ({g['version_source'] or '-'}) | {exe} "
              f"{ei.get('size', '?')} B {ei.get('date', '')}{miss}")
        if g.get("db"):
            print(f"    {db_text(g['db'])[0]}")
    return 0


def _size(n):
    for unit in ("B", "KB", "MB", "GB"):
        if n < 1024 or unit == "GB":
            return f"{n:.0f} {unit}" if unit == "B" else f"{n:.1f} {unit}"
        n /= 1024


def cmd_ls(sock, args):
    depth = 0 if args.recurse is None else args.recurse
    r = rpc(sock, {"op": "ls", "force": args.force, "path": args.path, "patterns": args.pattern, "recurse": depth,
                   "files_only": args.files, "limit": args.limit})
    if r is None:
        return 1
    files = 0
    for e in r["entries"]:
        if e.get("dir"):
            print(f"{'<DIR>':>10}  {'':16}  {e['path']}")
        elif e.get("error"):
            print(f"{'FEHLER':>10}  {'':16}  {e['path']}: {e['error']}")
        else:
            files += 1
            print(f"{_size(e['size']):>10}  {e['mtime']}  {e['path']}")
    print(f"{files} Dateien, {_size(r['total'])}" + (" (abgeschnitten, --limit)" if r["truncated"] else ""))
    return 0


def cmd_cat(sock, args):
    r = rpc(sock, {"op": "cat", "path": args.path, "grep": args.grep, "head": args.head, "tail": args.tail})
    if r is None:
        return 1
    if r["cut"]:
        print(f"(Datei {_size(r['size'])}, nur die letzten 4 MB gelesen)", file=sys.stderr)
    print("\n".join(r["lines"]))
    return 0


def cmd_ps(sock, args):
    r = rpc(sock, {"op": "ps", "filter": args.filter, "cmdline": args.cmdline})
    if r is None:
        return 1
    for p in r["procs"]:
        extra = f"  {p['cmdline']}" if p.get("cmdline") else ""
        print(f"{p['pid']:>6}  {p['mem_kb']:>8} KB  {p['name']}{extra}")
    if not r["procs"]:
        print("kein Prozess" + (f" mit '{args.filter}'" if args.filter else ""))
    return 0


def cmd_kill(sock, args):
    r = rpc(sock, {"op": "kill", "targets": args.targets, "tree": args.tree, "force": args.force})
    if r is None:
        return 1
    for k in r["killed"]:
        state = "läuft noch" if k["still_running"] else "beendet"
        print(f"{k['name']} {k['pid']}: {state}")
    return 0


def cmd_hash(sock, args):
    r = rpc(sock, {"op": "hash", "paths": args.paths, "algo": "sha1" if args.sha1 else "md5"})
    if r is None:
        return 1
    for p, h in r["hashes"].items():
        print(f"{h}  {p}")
    return 0


def cmd_copy(sock, args):
    send_json(sock, {"op": "copy", "src": args.src, "dst": args.dst, "verify": args.verify})
    r = recv_json(sock)
    for f in r.get("failed") or []:
        print(f"NICHT kopiert: {f['src']}: {f['error']}", file=sys.stderr)
    if "copied" in r:
        print(f"{len(r['copied'])} Dateien, {_size(r['bytes'])} in {r['seconds']} s nach {r['dst']}"
              + (", md5 geprüft" if args.verify else ""))
        if args.verify and len(r["copied"]) == 1:
            print(f"md5 {r['copied'][0]['md5']}")
    elif r.get("error"):
        print("Fehler:", r["error"], file=sys.stderr)
    return 0 if r.get("ok") else 1


def cmd_rm(sock, args):
    r = rpc(sock, {"op": "rm", "paths": args.paths, "recurse": args.recurse, "force": args.force})
    if r is None:
        return 1
    for p in r["removed"]:
        print(f"gelöscht: {p}")
    return 0


def cmd_disc(sock, args):
    r = rpc(sock, {"op": "disc", "force": args.force, "drive": args.drive})
    if r is None:
        return 1
    print(f"{r['drive']} Label {r['label']}")
    print("Wurzel: " + ", ".join(r["root"]))
    if r["protection"]:
        for k, v in r["protection"].items():
            print(f"Schutz {k}: {', '.join(v[:8])}" + (f" (+{len(v) - 8})" if len(v) > 8 else ""))
    else:
        print("Schutz: keine bekannten Dateien/Sektionen (dann ripiso, bei Lesefehlern Alcohol)")
    for e, sl in r["sections"].items():
        print(f"  {e}: {sl}")
    known = disc_lookup(r["label"], args.db)
    for d in known:
        flag = "" if d.get("verified", True) else " (nicht verifiziert)"
        print(f"Bekannt: {d['game']} {'/'.join(d['disc'])}: {d['protection']}{flag}, Image {d['image']}"
              + (f", Alcohol-Datentyp {d['alcohol_datatype']}" if d.get("alcohol_datatype") else ""))
        print(f"  Merkmal: {d['evidence']}" + (f"\n  Hinweis: {d['notes']}" if d.get("notes") else ""))
    if not known:
        print("Label nicht in disc-protection.json: nach dem Prüfen dort ergänzen")
    return 0


def disc_lookup(label, db=None):
    path = db or os.path.join(os.path.dirname(os.path.abspath(__file__)), "disc-protection.json")
    try:
        with open(path, encoding="utf-8") as f:
            discs = json.load(f)["discs"]
    except OSError:
        return []
    want = (label or "").strip().lower()
    return [d for d in discs if d.get("label") and d["label"].lower() == want]


def cmd_setupapi(sock, args):
    r = rpc(sock, {"op": "setupapi", "grep": args.grep, "last": args.last})
    if r is None:
        return 1
    print("\n\n".join(b.strip() for b in r["blocks"]))
    return 0


def _find_window(sock, pattern):
    import re
    top = rpc(sock, {"op": "ui"})
    pat = re.compile(pattern, re.I)
    for w in (top or {}).get("windows", []):
        if pat.search(w["text"]) or pat.search(w["exe"]):
            return w
    return None


def _press_label(sock, window, labels):
    w = _find_window(sock, window)
    if not w:
        return None
    r = rpc(sock, {"op": "ui", "hwnd": w["hwnd"]})
    for c in (r or {}).get("controls", []):
        if c["class"].lower() in BUTTON_CLASSES and c["enabled"] and _label_norm(c["text"]) in labels:
            rpc(sock, {"op": "press", "hwnd": c["hwnd"]})
            return c["text"]
    return None


def _install_eula(sock, args, res):
    """GOG-Inno mit EULA-Checkbox: interaktiv starten, Checkbox anklicken, Installieren, am Ende Verlassen."""
    cmd = f'"{args.setup}" /SP- /LANG={args.lang} "/DIR={args.dir}" "/LOG={res["log"]}"'
    rpc(sock, {"op": "run", "cmd": cmd, "detach": True})
    w = None
    for _ in range(60):
        time.sleep(2)
        w = _find_window(sock, r"\.tmp$")
        if w and w["rect"][2] - w["rect"][0] > 300:
            break
    if not w:
        print("Setup-Fenster nicht gefunden", file=sys.stderr)
        return 1
    l, t, rgt, b = w["rect"]
    rpc(sock, {"op": "click", "x": (l + rgt) // 2 - 88, "y": b - 28, "button": "left"})
    time.sleep(1)
    if not _press_label(sock, r"\.tmp$", {"installieren", "install"}):
        print("Button Installieren nicht gefunden (Checkbox nicht getroffen?), per shot prüfen", file=sys.stderr)
        return 1
    print("EULA angehakt, installiere ...")
    deadline = time.time() + args.timeout
    while time.time() < deadline:
        time.sleep(5)
        r = rpc(sock, {"op": "cat", "path": res["log"], "tail": 5})
        text = "\n".join((r or {}).get("lines", []))
        if "Current Page: Finished" in text or "Log closed" in text:
            break
    _press_label(sock, r"\.tmp$", {"verlassen", "exit", "fertigstellen", "finish"})
    r = rpc(sock, {"op": "cat", "path": res["log"], "grep": "Installation process succeeded"})
    ok = bool(r and r.get("lines"))
    print("fertig: Installation process succeeded" if ok else f"nicht bestätigt, Log prüfen: {res['log']}")
    return 0 if ok else 1


def cmd_install(sock, args):
    req = {"op": "install", "setup": args.setup, "dir": args.dir, "kind": args.kind, "lang": args.lang,
           "args": args.args, "force": args.force, "timeout": args.timeout, "detect_only": args.detect}
    r = rpc(sock, req)
    if r is None:
        return 1
    if args.detect:
        print(f"Typ: {r['kind']}")
        return 0
    if r["status"] == "unbekannt":
        print(f"Typ {r['kind']}: {r['hint']}")
        return 1
    if "exit" not in r:
        # Zeitüberschreitung: Server schickt nur status, kind, cmd, detail
        print(f"FEHLER: {r.get('detail', r['status'])}\n  {r.get('cmd', '')}")
        return 1
    print(f"Typ {r['kind']}, {r['seconds']} s, Exit {r['exit']}, {r['files']} Dateien in {args.dir}, Log {r['log']}")
    if r["status"] == "eula":
        print("Silent an der EULA-Seite abgebrochen (GOG), interaktiv mit Checkbox")
        return _install_eula(sock, args, r)
    if r["status"] == "fehler" and r["kind"] == "inno" and r.get("exit") == 0 and r.get("files") and not r.get("tail"):
        # altes Inno Setup (4.x) kennt /LOG nicht: kein Log, dann zählen Exit-Code und Dateien
        print("ok (kein Inno-Log, altes Setup? Exit 0 und Dateien vorhanden)")
        return 0
    if r["status"] != "ok":
        print("FEHLER, Log-Ende:")
        for l in r["tail"]:
            print("  " + l)
        return 1
    print("ok")
    return 0


def cmd_uninstall(sock, args):
    r = rpc(sock, {"op": "uninstall", "name": args.name, "run": args.run})
    if r is None:
        return 1
    for e in r["entries"]:
        print(f"{e['name']} {e['version']} [{e['key']}]\n  {e['uninstall']}")
    if r.get("started"):
        print(f"gestartet: {r['started']} (Dialoge mit ui/press bedienen)")
    elif not r["entries"]:
        print("kein Eintrag")
    return 0



GAMECHECK_LABEL = {"patch": "Patch da", "unklar": "unklar", "aktuell": "aktuell", "fehlt": "Exe fehlt", "unbekannt": "unbekannt"}


def cmd_gamecheck(sock, args):
    """Bekannte Spiele mit installierter und neuester Version (Server v17, op gamescan)."""
    if args.push_db:
        r = rpc(sock, {"op": "ping"})
        if r is None:
            return 1
        db = Path(__file__).resolve().with_name("game-versions.json")
        target = r["script"].rsplit("\\", 1)[0] + "\\game-versions.json"
        _put_bytes(sock, db.read_bytes(), target)
        print(f"Datenbank hochgeladen: {Path(db).name} (Stand {json.loads(db.read_text(encoding='utf-8'))['stand']}). "
              "Sie gilt nur, wenn ihr Stand neuer ist als der in xpserver.exe eingebaute.")
    r = rpc(sock, {"op": "gamescan", "hash": not args.fast})
    if r is None:
        return 1
    rows = r["games"]
    if args.json:
        print(json.dumps(r, ensure_ascii=False, indent=1))
        return 0
    shown = rows if args.alle else [g for g in rows if g["status"] not in ("aktuell", "unbekannt")]
    for g in shown:
        lab = GAMECHECK_LABEL.get(g["status"], g["status"])
        ver = g.get("version") or g.get("problem") or g["exe"].rsplit("\\", 1)[-1]
        if g.get("confidence") == "niedrig":
            ver = "? " + ver
        line = f"{lab:10} {g['name'][:34]:34} {ver[:44]:44}"
        if g.get("latest") and g["status"] != "aktuell":
            line += f" -> {g['latest']}"
        print(line.rstrip())
    count = {}
    for g in rows:
        count[g["status"]] = count.get(g["status"], 0) + 1
    print(("\n" if shown else "") + ", ".join(f"{count[s]} {GAMECHECK_LABEL[s]}" for s in GAMECHECK_LABEL if s in count)
          + f"  (Datenbank Stand {r['stand']}, {r['db_source']}; laden {r['db_ms']} ms, suchen {r['find_ms']} ms, "
          f"md5 {r['hashed']} neu in {r['hash_ms']} ms, {r['cached']} aus dem Cache)")
    if not args.alle:
        print("Alle zeigen: --alle")
    return 0


def cmd_ver(sock, args):
    r = rpc(sock, {"op": "ver", "paths": args.paths})
    if r is None:
        return 1
    for p, v in r["files"].items():
        if v.get("error"):
            print(f"{p}: Fehler {v['error']}")
            continue
        st = v.get("strings") or {}
        print(f"{p}\n  Datei {v.get('file_version') or '-'}  Produkt {v.get('product_version') or '-'}  "
              f"{v.get('size')} B  {v.get('date')}  md5 {v.get('md5') or '-'}")
        for k in ("ProductName", "FileDescription", "CompanyName", "FileVersion", "ProductVersion"):
            if st.get(k):
                print(f"  {k}: {st[k]}")
    return 0


def cmd_ini(sock, args):
    rc = 0
    for path in args.files:
        r = rpc(sock, {"op": "ini", "path": path, "section": args.section, "key": args.key, "value": args.value})
        if r is None:
            rc = 1
            continue
        if args.value is None:
            print(f"{path}: {args.key}={r['old']}")
        elif r["changed"]:
            print(f"{path}: {args.key} {r['old']} -> {args.value} (Sicherung {r['backup']})")
        else:
            print(f"{path}: {args.key} schon {args.value}")
    return rc


def cmd_nv(sock, args):
    req = {"op": "nv"}
    if args.setting and args.value:
        req["set"] = [args.setting, args.value]
    elif args.setting:
        req["names"] = [args.setting]
    r = rpc(sock, req)
    if r is None:
        return 1
    for k, v in r["settings"].items():
        print(f"{k}: {v}")
    return 0


def cmd_toc(sock, args):
    r = rpc(sock, {"op": "toc", "force": args.force, "drive": args.drive})
    if r is None:
        return 1
    print(f"{args.drive} Label {r['label']}")
    for t in r["tracks"]:
        kind = "Daten" if t["data"] else "Audio"
        print(f"  Spur {t['track']:2d} {kind:5s} LBA {t['lba']:6d}  {t['sectors']:6d} Sektoren  {t['sectors'] / 75 / 60:5.1f} min")
    return 0


def cmd_ripcd(sock, args):
    r = rpc(sock, {"op": "ripcd", "force": args.force, "drive": args.drive, "folder": args.folder, "name": args.name})
    if r is None:
        return 1
    print(f"{r['cue']}: Label {r['label']}, {len(r['tracks'])} Spuren, {r['seconds']} s")
    for t in r["tracks"]:
        bad = f"  {t['bad']} unlesbar ({t['bad_range'][0]}-{t['bad_range'][1]}), Nullen" if t["bad"] else ""
        print(f"  {t['file']}: {t['sectors']} Sektoren md5 {t['md5']}{bad}")
    return 0


def _label_norm(s):
    return " ".join(s.replace("&", "").split()).lower()


def cmd_press(sock, args):
    if args.hwnd:
        r = rpc(sock, {"op": "press", "hwnd": int(args.hwnd, 0)})
        if r:
            print(f"ok ({r['how']})")
        return 0 if r else 1
    if not args.label:
        sys.exit("press braucht HWND oder --label TEXT")
    import re
    top = rpc(sock, {"op": "ui"})
    if top is None:
        return 1
    want = _label_norm(args.label)
    pat = re.compile(args.window, re.I) if args.window else None
    for w in top["windows"]:
        if pat and not (pat.search(w["text"]) or pat.search(w["exe"])):
            continue
        if not w["enabled"] or w["class"].lower() in HIDE_TOP:
            continue
        r = rpc(sock, {"op": "ui", "hwnd": w["hwnd"]})
        if r is None:
            continue
        hit = next((c for c in r["controls"] if c["class"].lower() in BUTTON_CLASSES and c["enabled"]
                    and _label_norm(c["text"]) == want), None)
        if hit:
            p = rpc(sock, {"op": "press", "hwnd": hit["hwnd"]})
            if p is None:
                return 1
            print(f"gedrückt: \"{hit['text']}\" in {w['hwnd']:#x} \"{one(w['text'], 50)}\"")
            time.sleep(args.wait)
            return cmd_ui(sock, argparse.Namespace(hwnd=None, all=False, json=False))
    print(f"kein aktiver Button \"{args.label}\" gefunden" + (f" (Fenster ~ {args.window})" if args.window else ""),
          file=sys.stderr)
    return 1


def main() -> int:
    ap = argparse.ArgumentParser(description="xpctl (Client für xpserver)")
    ap.add_argument("--host", default=os.environ.get("XPCTL_HOST"), help="IP/Name des XP-Rechners (oder XPCTL_HOST)")
    ap.add_argument("--port", type=int, default=int(os.environ.get("XPCTL_PORT", "5050")))
    sub = ap.add_subparsers(dest="cmd", required=True)

    sub.add_parser("ping").set_defaults(fn=cmd_ping)

    p = sub.add_parser("run")
    p.add_argument("--timeout", type=float, default=60)
    p.add_argument("--cwd")
    p.add_argument("--detach", action="store_true", help="starten und nicht warten (GUI/Installer)")
    p.add_argument("command", nargs=argparse.REMAINDER)
    p.set_defaults(fn=cmd_run)

    p = sub.add_parser("put")
    p.add_argument("local")
    p.add_argument("remote")
    p.add_argument("--mkdir", action="store_true", help="Zielordner anlegen")
    p.set_defaults(fn=cmd_put)

    p = sub.add_parser("get")
    p.add_argument("remote")
    p.add_argument("local")
    p.set_defaults(fn=cmd_get)

    p = sub.add_parser("ui", help="Fenster und Controls als Text (billig, zuerst benutzen)")
    p.add_argument("--hwnd", help="Controls dieses Fensters statt des Vordergrundfensters")
    p.add_argument("--all", action="store_true", help="auch unsichtbare/leere Einträge")
    p.add_argument("--json", action="store_true")
    p.set_defaults(fn=cmd_ui)

    p = sub.add_parser("shot", help="Screenshot als PNG (Standard: halbe Größe)")
    p.add_argument("--hwnd", help="nur dieses Fenster")
    p.add_argument("--region", help="l,t,r,b in Bildschirmkoordinaten")
    p.add_argument("--scale", type=float, default=0.5)
    p.add_argument("--full", action="store_true", help="volle Größe")
    p.add_argument("--out")
    p.set_defaults(fn=cmd_shot)

    p = sub.add_parser("click", help="Mausklick auf Bildschirmkoordinaten")
    p.add_argument("x", type=int)
    p.add_argument("y", type=int)
    p.add_argument("--right", action="store_true")
    p.add_argument("--double", action="store_true")
    p.add_argument("--move", action="store_true", help="nur bewegen")
    p.set_defaults(fn=cmd_click)

    p = sub.add_parser("press", help="Button auslösen: per hwnd oder --label (zeigt danach den nächsten Dialog)")
    p.add_argument("hwnd", nargs="?")
    p.add_argument("--label", help='Beschriftung, z.B. "Weiter >" (& und Groß/klein egal)')
    p.add_argument("--window", help="Regex auf Fenstertitel oder Exe")
    p.add_argument("--wait", type=float, default=3, help="Sekunden bis zur Anzeige des nächsten Dialogs")
    p.set_defaults(fn=cmd_press)

    # Spiele-Werkzeuge (Server v6)
    p = sub.add_parser("lnk", help="Verknüpfungen: show | make | launcher | icon | style")
    ls = p.add_subparsers(dest="action", required=True)
    q = ls.add_parser("show", help="Ziel, Argumente, Ordner, Icon, Fensterart")
    q.add_argument("names", nargs="+", help="Name (eindeutiger Anfang) oder voller .lnk-Pfad")
    q = ls.add_parser("make", help="Verknüpfung ohne CD anlegen/ändern, danach einsortieren")
    q.add_argument("name")
    q.add_argument("target")
    q.add_argument("--args", default="")
    q.add_argument("--dir", help="Arbeitsordner (Standard: Ordner des Ziels)")
    q.add_argument("--icon", help="Datei,Index")
    q.add_argument("--folder", help="Gruppenordner direkt (sonst nach der Gruppendatei, XPCTL_GROUPS)")
    q.add_argument("--no-group", action="store_true")
    q = ls.add_parser("launcher", help="Starter: mountet IMAGE auf G: und startet EXE (launch.vbs)")
    q.add_argument("name")
    q.add_argument("image", help="Pfad des Images aus Sicht des XP-Rechners (lokal oder UNC)")
    q.add_argument("dir", help="Spielordner")
    q.add_argument("exe", help="Exe relativ zum Spielordner")
    q.add_argument("--icon", help="Datei,Index (Standard: Spiel-Exe)")
    q.add_argument("--folder")
    q.add_argument("--no-group", action="store_true")
    q = ls.add_parser("icon")
    q.add_argument("name")
    q.add_argument("icon", help="Datei,Index")
    q = ls.add_parser("style", help="Fensterart")
    q.add_argument("name")
    q.add_argument("show", choices=["normal", "max", "min"])
    p.set_defaults(fn=cmd_lnk)

    p = sub.add_parser("desk", help="Desktop-Gruppen: list | move | foldericon | group | ungroup")
    ds = p.add_subparsers(dest="action", required=True)
    q = ds.add_parser("list")
    q.add_argument("filter", nargs="?")
    q = ds.add_parser("move", help='in Gruppenordner, "-" = zurück auf den Desktop')
    q.add_argument("name")
    q.add_argument("folder")
    q = ds.add_parser("foldericon", help='Datei,Index oder "@<Verknüpfung>"')
    q.add_argument("folder")
    q.add_argument("icon")
    q = ds.add_parser("group", help="nach Gruppendatei einsortieren (--map oder XPCTL_GROUPS), Ordner-Icons setzen")
    q.add_argument("--map", help="lokale Zuordnungsdatei (Standard: XPCTL_GROUPS)")
    ds.add_parser("ungroup")
    p.set_defaults(fn=cmd_desk)

    p = sub.add_parser("deskpos", help="Desktop-Icons verschieben: list | move | grid | row")
    ps = p.add_subparsers(dest="action", required=True)
    ps.add_parser("list")
    q = ps.add_parser("move", help="Pixel")
    q.add_argument("name")
    q.add_argument("x", type=int)
    q.add_argument("y", type=int)
    q = ps.add_parser("grid", help="Raster, negativ = von rechts/unten")
    q.add_argument("name")
    q.add_argument("col", type=int)
    q.add_argument("row", type=int)
    q = ps.add_parser("row", help="Namen der Reihe nach in eine Zeile, --rest = alle außer System-Icons")
    q.add_argument("row", type=int)
    q.add_argument("names", nargs="*")
    q.add_argument("--rest", action="store_true")
    p.set_defaults(fn=cmd_deskpos)

    p = sub.add_parser("mount", help="Image per Alcohol einhängen (verweigert, wenn ein Spiel vorne läuft)")
    p.add_argument("image")
    p.add_argument("--drive", help="virtuelles Laufwerk (Standard: image_drive des Servers)")
    p.add_argument("--force", action="store_true")
    p.set_defaults(fn=cmd_mount)

    p = sub.add_parser("unmount", help="Alcohol-Laufwerk leeren")
    p.add_argument("--drive", help="virtuelles Laufwerk (Standard: image_drive des Servers)")
    p.add_argument("--force", action="store_true")
    p.set_defaults(fn=cmd_unmount)

    p = sub.add_parser("eject", help="physisches Laufwerk auswerfen")
    p.add_argument("--drive", help="Laufwerk (Standard: cd_drive des Servers oder erstes CD-Laufwerk)")
    p.add_argument("--force", action="store_true", help="auch wenn ein Alcohol-Assistent offen ist")
    p.set_defaults(fn=cmd_eject)

    p = sub.add_parser("ripiso", help="ungeschützte CD 1:1 als ISO lesen (geschützte: Alcohol MDF/MDS)")
    p.add_argument("target", help="Zieldatei auf dem XP-Rechner, z. B. Ordner\\Spiel.iso")
    p.add_argument("--drive", help="CD-Laufwerk (Standard wie bei eject)")
    p.add_argument("--force", action="store_true", help="auch wenn ein Alcohol-Assistent offen ist")
    p.set_defaults(fn=cmd_ripiso)

    p = sub.add_parser("ini", help="Wert in INI/CONF lesen oder setzen (Sicherung .xpctl-bak), mehrere Dateien möglich")
    p.add_argument("key")
    p.add_argument("files", nargs="+")
    p.add_argument("--section", "-s")
    p.add_argument("--value", "-v", help="ohne: nur lesen")
    p.set_defaults(fn=cmd_ini)

    p = sub.add_parser("nv", help="globale NVIDIA-3D-Einstellung lesen/setzen (vsync app|on|off, triplebuffer on|off, prerender N)")
    p.add_argument("setting", nargs="?", choices=["vsync", "triplebuffer", "prerender"])
    p.add_argument("value", nargs="?")
    p.set_defaults(fn=cmd_nv)

    p = sub.add_parser("toc", help="Spuren der CD (Daten/Audio, Länge)")
    p.add_argument("--drive", default="E:")
    p.add_argument("--force", action="store_true", help="auch wenn ein Alcohol-Assistent offen ist")
    p.set_defaults(fn=cmd_toc)

    p = sub.add_parser("ripcd", help="CD mit Audiospuren als CUE + ISO/BIN je Spur (Mixed-Mode, DOSBox)")
    p.add_argument("folder", help=r"Zielordner, z.B. C:\Rips\Bleifuss")
    p.add_argument("name", help="Dateiname ohne Endung")
    p.add_argument("--drive", default="E:")
    p.add_argument("--force", action="store_true", help="auch wenn ein Alcohol-Assistent offen ist")
    p.set_defaults(fn=cmd_ripcd)

    p = sub.add_parser("pesec", help="PE-Sektionen einer Exe (stxt774/stxt371 = SafeDisc 2+, .securom)")
    p.add_argument("paths", nargs="+")
    p.set_defaults(fn=cmd_pesec)

    p = sub.add_parser("driver", help="Geräte auflisten, INF-Treiber installieren (Logo-Dialog per press)")
    p.add_argument("action", choices=["list", "install"])
    p.add_argument("target", nargs="?", help="list: Filter (Teilstring von ID/Name), install: INF-Pfad auf dem XP")
    p.add_argument("--hwid", help="nur diese Hardware-ID (Standard: alle angeschlossenen, die in der INF stehen)")
    p.set_defaults(fn=cmd_driver)

    p = sub.add_parser("games", help="Spiele-Icons mit Exe-Version, GOG-Info, Uninstall-Eintrag")
    p.add_argument("filter", nargs="?", help="Teil des Icon-Namens")
    p.add_argument("--json", help="Rohdaten als JSON in diese Datei (md5/Größe für neue Datenbank-Einträge)")
    p.add_argument("--db", help="Versionsdatenbank (Standard: game-versions.json neben xpctl.py)")
    p.add_argument("--gog-ssh", action="store_true",
                   help="GOG-Version aus den Setup-Dateinamen auf einem SSH-Host ergänzen (XPCTL_GOG_SSH, XPCTL_GOG_DIR)")
    p.set_defaults(fn=cmd_games)

    p = sub.add_parser("gamecheck", help="bekannte Spiele: installierte und neueste Version (Server v17)")
    p.add_argument("--alle", action="store_true", help="auch aktuelle und unbekannte zeigen")
    p.add_argument("--fast", action="store_true", help="ohne md5, nur Exe-Name und Größe")
    p.add_argument("--push-db", action="store_true", help="vorher client/game-versions.json neben xpserver.exe legen")
    p.add_argument("--json", action="store_true")
    p.set_defaults(fn=cmd_gamecheck)

    p = sub.add_parser("ls", help="Dateien mit Größe/Datum (statt run dir)")
    p.add_argument("path")
    p.add_argument("-r", "--recurse", type=int, nargs="?", const=-1, help="Tiefe, ohne Zahl = alles")
    p.add_argument("-p", "--pattern", nargs="+", help="Dateimuster, z.B. *.exe *.iso")
    p.add_argument("--files", action="store_true", help="nur Dateien")
    p.add_argument("--limit", type=int, default=2000)
    p.add_argument("--force", action="store_true", help="auch wenn ein Alcohol-Assistent offen ist")
    p.set_defaults(fn=cmd_ls)

    p = sub.add_parser("cat", help="Textdatei lesen/filtern (statt run type/findstr)")
    p.add_argument("path")
    p.add_argument("-g", "--grep", help="Teilstring (ohne Groß/klein) oder /regex/")
    p.add_argument("--head", type=int)
    p.add_argument("--tail", type=int)
    p.set_defaults(fn=cmd_cat)

    p = sub.add_parser("ps", help="Prozesse (statt run tasklist|findstr)")
    p.add_argument("filter", nargs="?")
    p.add_argument("--cmdline", action="store_true", help="Befehlszeilen (wmic)")
    p.set_defaults(fn=cmd_ps)

    p = sub.add_parser("kill", help="Prozesse beenden (statt run taskkill), prüft danach")
    p.add_argument("targets", nargs="+", help="PID oder exakter Name (xcopy.exe)")
    p.add_argument("--tree", action="store_true")
    p.add_argument("--force", action="store_true", help="auch Explorer/Python/Systemprozesse")
    p.set_defaults(fn=cmd_kill)

    p = sub.add_parser("hash", help="md5/sha1 von Dateien auf dem XP")
    p.add_argument("paths", nargs="+")
    p.add_argument("--sha1", action="store_true")
    p.set_defaults(fn=cmd_hash)

    p = sub.add_parser("copy", help="Datei/Ordner kopieren, unlesbare Dateien melden (statt run copy/xcopy)")
    p.add_argument("src")
    p.add_argument("dst", help=r"Ziel, mit \ am Ende = in den Ordner")
    p.add_argument("--verify", action="store_true", help="md5 Quelle/Ziel vergleichen")
    p.set_defaults(fn=cmd_copy)

    p = sub.add_parser("rm", help="löschen (ohne --force nur in rm_roots der Server-Konfiguration und auf den Desktops)")
    p.add_argument("paths", nargs="+")
    p.add_argument("-r", "--recurse", action="store_true")
    p.add_argument("--force", action="store_true")
    p.set_defaults(fn=cmd_rm)

    p = sub.add_parser("disc", help="CD prüfen: Label, Kopierschutz-Dateien, PE-Sektionen")
    p.add_argument("drive", nargs="?", help="CD-Laufwerk (Standard wie bei eject)")
    p.add_argument("--db", help="Kopierschutz-Datenbank (Standard: disc-protection.json neben xpctl.py)")
    p.add_argument("--force", action="store_true", help="auch wenn ein Alcohol-Assistent offen ist")
    p.set_defaults(fn=cmd_disc)

    p = sub.add_parser("ver", help="Version/Größe/Datum/md5 von Exe- und DLL-Dateien")
    p.add_argument("paths", nargs="+")
    p.set_defaults(fn=cmd_ver)

    p = sub.add_parser("install", help="Installer still ausführen (Inno/NSIS/MSI erkannt), GOG-EULA automatisch, Log prüfen")
    p.add_argument("setup", help=r"z.B. \\server\share\setup_x.exe")
    p.add_argument("dir", help=r"Ziel, z.B. C:\Games\Spiel (muss leer sein, sonst --force)")
    p.add_argument("--kind", default="auto", choices=["auto", "inno", "nsis", "msi"])
    p.add_argument("--lang", default="german")
    p.add_argument("--args", default="", help="zusätzliche Installer-Argumente")
    p.add_argument("--force", action="store_true", help="auch in nicht leeren Zielordner")
    p.add_argument("--timeout", type=float, default=1800)
    p.add_argument("--detect", action="store_true", help="nur Installer-Typ anzeigen")
    p.set_defaults(fn=cmd_install)

    p = sub.add_parser("uninstall", help="Programm deinstallieren (Uninstall-Eintrag), Dialoge danach per press")
    p.add_argument("name", help="Teil von Anzeigename oder Schlüssel")
    p.add_argument("--run", action="store_true", help="Deinstaller starten (sonst nur anzeigen)")
    p.set_defaults(fn=cmd_uninstall)

    p = sub.add_parser("setupapi", help="letzte Treiberinstallationen aus setupapi.log")
    p.add_argument("-g", "--grep")
    p.add_argument("--last", type=int, default=5)
    p.set_defaults(fn=cmd_setupapi)

    p = sub.add_parser("vol", help="Datenträgerbezeichnung der Laufwerke")
    p.add_argument("drives", nargs="*")
    p.add_argument("--force", action="store_true", help="auch wenn ein Alcohol-Assistent offen ist")
    p.set_defaults(fn=cmd_vol)

    p = sub.add_parser("settext", help="Text direkt in ein Edit-Control schreiben")
    p.add_argument("hwnd")
    p.add_argument("text")
    p.set_defaults(fn=cmd_settext)

    p = sub.add_parser("type", help="Text tippen (an das Fenster mit Fokus)")
    p.add_argument("text")
    p.set_defaults(fn=cmd_type)

    p = sub.add_parser("key", help="Tasten, z.B. ENTER TAB ALT+F4 CTRL+A")
    p.add_argument("keys", nargs="+")
    p.set_defaults(fn=cmd_key)

    p = sub.add_parser("focus", help="Fenster nach vorne")
    p.add_argument("hwnd")
    p.set_defaults(fn=cmd_focus)

    p = sub.add_parser("close", help="WM_CLOSE an ein Fenster")
    p.add_argument("hwnd")
    p.set_defaults(fn=cmd_close)

    p = sub.add_parser("win", help="Fenster verschieben (move) oder als Gitter anordnen (tile)")
    ws = p.add_subparsers(dest="action", required=True)
    q = ws.add_parser("move", help="HWND X Y BREITE HÖHE (Bildschirmpixel)")
    q.add_argument("hwnd")
    for k in ("x", "y", "w", "h"):
        q.add_argument(k, type=int)
    q = ws.add_parser("tile", help="alle Fenster, deren Titel auf REGEX passt, nach Titel sortiert im Gitter")
    q.add_argument("title", help="Regex auf den Fenstertitel")
    q.add_argument("--exe", help="nur Fenster dieses Prozesses, z.B. Explorer.EXE")
    q.add_argument("--cols", type=int, default=0, help="Spalten (Standard: Wurzel der Anzahl)")
    q.add_argument("--gap", type=int, default=0, help="Abstand in Pixeln")
    q.add_argument("--taskbar", type=int, default=30, help="unten freilassen (Taskleiste)")
    p.set_defaults(fn=cmd_win)

    p = sub.add_parser("updates", help="installierte Hotfixes/Updates, SP-Stand, POSReady")
    p.add_argument("--grep", help="Filter (Teilstring, ohne Groß/Klein)")
    p.add_argument("--json", action="store_true")
    p.set_defaults(fn=cmd_updates)

    p = sub.add_parser("programs", help="installierte Programme (ohne Updates)")
    p.add_argument("--all", action="store_true", help="auch Updates und Systemkomponenten")
    p.add_argument("--grep", help="Filter (Name, Hersteller, Version)")
    p.add_argument("--json", action="store_true")
    p.set_defaults(fn=cmd_programs)

    p = sub.add_parser("sysinfo", help="Windows, CPU, RAM, Laufwerke, IE, DirectX, .NET")
    p.add_argument("--json", action="store_true")
    p.set_defaults(fn=cmd_sysinfo)

    p = sub.add_parser("temps", help="Temperaturen: ACPI, SMART, NVIDIA, HWiNFO, SpeedFan (xpserver.exe)")
    p.add_argument("--source", action="append", metavar="QUELLE",
                   help="nur diese Quelle(n), kommagetrennt oder mehrfach: " + ", ".join(TEMP_SOURCES))
    p.add_argument("--json", action="store_true")
    p.set_defaults(fn=cmd_temps)

    p = sub.add_parser("saves", help="Spielstände und Profile lesen und ändern (pro Spiel und Exe-Version)")
    p.add_argument("game", nargs="?", help="Spiel-Id (z. B. nfsu) oder 'formats'; ohne: Übersicht")
    p.add_argument("action", nargs="?", help="show, set, repair, backup (oder direkt ein Profilname)")
    p.add_argument("profile", nargs="?")
    p.add_argument("assign", nargs="*", metavar="FELD=WERT")
    p.add_argument("--json", action="store_true")
    p.add_argument("--dry-run", action="store_true", help="nur zeigen, was sich ändern würde")
    p.add_argument("--force", action="store_true",
                   help="auch schreiben, wenn das Spiel läuft (Hauptmenü, Profil nicht geladen); "
                        "Spiele, die ihre Dateien selbst neu schreiben (NFSU), lehnen das ab")
    p.set_defaults(fn=cmd_saves)

    p = sub.add_parser("reg", help="Registry-Schlüssel lesen (nur lesen)")
    p.add_argument("key", help=r"z.B. HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion")
    p.add_argument("--values", action="store_true", help="Werte auch bei Unterschlüsseln (mit --recurse)")
    p.add_argument("--recurse", type=int, default=0, help="Tiefe der Unterschlüssel (max 8)")
    p.add_argument("--json", action="store_true")
    p.set_defaults(fn=cmd_reg)

    p = sub.add_parser("settings", help="kuratierte Einstellungen (Explorer, Design, Region, Konten, Ruhe, Netz, Tuning)")
    p.add_argument("--only", help="Abschnitte: explorer,design,region,users,quiet,network,gaming,hardware")
    p.add_argument("--paths", action="store_true", help="Registry-Pfad je Eintrag")
    p.add_argument("--json", action="store_true")
    p.set_defaults(fn=cmd_settings)

    p = sub.add_parser("productkey", help="XP-Produktschlüssel in eine Datei schreiben (nicht ins Terminal)")
    p.add_argument("--out", required=True, help="Zieldatei (chmod 600)")
    p.set_defaults(fn=cmd_productkey)

    p = sub.add_parser("update", help="Server auf dem XP durch die neue Version ersetzen und neu starten")
    p.add_argument("--file", help="andere Server-Datei (Standard: server/dist/xpserver.exe bzw. server/bin/Release/net35)")
    p.add_argument("--force", action="store_true", help="auch senden, wenn die Prüfsumme gleich ist")
    p.set_defaults(fn=cmd_update)

    p = sub.add_parser("migrate", help="alten Python-Server auf dem XP durch xpserver.exe (.NET) ersetzen")
    p.add_argument("--file", help="xpserver.exe (Standard: server/dist bzw. server/bin/Release/net35)")
    p.add_argument("--allow", help="erlaubtes Netz für xpserver.json, falls keine existiert (Standard: eigenes /24)")
    p.add_argument("--no-autostart", action="store_true", help="Autostart nicht anfassen")
    p.set_defaults(fn=cmd_migrate)

    args = ap.parse_args()
    if not args.host:
        ap.error("kein Host: --host oder XPCTL_HOST setzen")
    if args.cmd == "run" and not args.command:
        ap.error("run braucht einen Befehl")
    key = load_key()
    try:
        sock = connect(args.host, args.port, key)
    except OSError as e:
        sys.exit(f"keine Verbindung zu {args.host}:{args.port}: {e}")
    try:
        rc = args.fn(sock, args)
        try:
            send_json(sock, {"op": "bye"})
        except OSError:
            pass
        return rc
    finally:
        sock.close()


if __name__ == "__main__":
    sys.exit(main())
