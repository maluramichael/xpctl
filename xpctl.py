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
  xpctl.py reg KEY [--values] [--recurse N] [--json]   Registry-Schlüssel lesen (HKLM/HKCU/HKU/HKCR)
  xpctl.py settings [--only abschnitt,...] [--paths] [--json]   kuratierte Einstellungen
  xpctl.py productkey --out DATEI              XP-Produktschlüssel nur in Datei (chmod 600)
  xpctl.py update [--file xpserver.py]   (Server auf dem XP ersetzen + neu starten)
  Spiele-Werkzeuge (Server v6):
  xpctl.py press --label "Weiter >" [--window REGEX]   Button per Beschriftung, zeigt danach den Dialog
  xpctl.py lnk show|make|launcher|icon|style ...       Verknüpfungen (ohne VBS, IShellLink per ctypes)
  xpctl.py desk list|move|foldericon|group|ungroup     Gruppenordner nach desktop-gruppen.txt
  xpctl.py deskpos list|move|grid|row                  Desktop-Icons verschieben
  xpctl.py mount IMG | unmount | eject | vol           Alcohol-Laufwerk G:, CD-Laufwerk E:
  xpctl.py ripiso ZIEL.iso | pesec EXE...                CD als ISO lesen, Kopierschutz-Sektionen
  xpctl.py driver list [FILTER] | install INF [--hwid ID]   Geräte, INF-Treiber (Server v7)
  xpctl.py games [FILTER] [--db DATEI] [--json DATEI]            Spiele-Icons: Exe-Version, GOG-Info, Uninstall (Server v8)
  Dateien/Prozesse/Discs (Server v9, statt run mit dir/findstr/type/tasklist/taskkill/xcopy/del):
  xpctl.py ls PFAD [-r [TIEFE]] [-p MUSTER...] [--files]   Größe, Datum, Summe
  xpctl.py cat DATEI [-g TEXT|/regex/] [--head N] [--tail N]
  xpctl.py ps [FILTER] [--cmdline] | kill NAME|PID... [--tree] [--force]
  xpctl.py hash DATEI... [--sha1] | copy QUELLE ZIEL [--verify] | rm PFAD... [-r] [--force]
  xpctl.py disc [E:]          Label, Wurzel, Kopierschutz-Dateien, PE-Sektionen der Exes
  xpctl.py setupapi [-g TEXT] [--last N]   letzte Treiberinstallationen aus setupapi.log
  xpctl.py uninstall NAME [--run]          Uninstall-Eintrag zeigen, mit --run Deinstaller starten

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


def cmd_update(sock, args):
    src = Path(args.file) if args.file else Path(__file__).resolve().with_name("xpserver.py")
    data = src.read_bytes()
    want = hashlib.sha256(data).hexdigest()
    send_json(sock, {"op": "ping"})
    before = recv_json(sock)
    if "sha256" not in before:
        print("Der Server auf dem XP ist v1 und kennt kein update. Einmal von Hand "
              "xpserver.py + xpserver.bat auf den XP-Rechner kopieren und neu starten.",
              file=sys.stderr)
        return 1
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
    key = load_key()
    deadline = time.time() + 40
    time.sleep(1.5)
    while time.time() < deadline:
        try:
            s2 = connect(args.host, args.port, key)
        except (OSError, SystemExit):
            time.sleep(1)
            continue
        send_json(s2, {"op": "ping"})
        after = recv_json(s2)
        send_json(s2, {"op": "bye"})
        s2.close()
        if after["sha256"] == want:
            print(f"neu gestartet, läuft v{after['version']} (sha256 {want[:12]})")
            return 0
        print(f"Server läuft, aber mit alter/anderer Version (v{after['version']}): "
              "vermutlich Rollback nach Startfehler, siehe Konsole auf dem XP.", file=sys.stderr)
        return 1
    print("Server nach 40 s nicht wieder erreichbar, siehe Konsole auf dem XP.", file=sys.stderr)
    return 1


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
    return 0 if rpc(sock, {"op": "eject", "drive": args.drive}) else 1


def cmd_vol(sock, args):
    r = rpc(sock, {"op": "vol", "drives": args.drives or None})
    if r is None:
        return 1
    for d, l in sorted(r["drives"].items()):
        print(f"{d} {l if l is not None else '(kein Medium)'}")
    return 0


def cmd_ripiso(sock, args):
    r = rpc(sock, {"op": "ripiso", "drive": args.drive, "target": args.target})
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
            g["db"] = {k: hit.get(k) for k in ("game", "version", "latest", "confidence")}
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
    r = rpc(sock, {"op": "ls", "path": args.path, "patterns": args.pattern, "recurse": depth,
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
    r = rpc(sock, {"op": "disc", "drive": args.drive})
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
        hit = next((c for c in r["controls"] if c["class"].lower() == "button" and c["enabled"]
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
    p.set_defaults(fn=cmd_eject)

    p = sub.add_parser("ripiso", help="ungeschützte CD 1:1 als ISO lesen (geschützte: Alcohol MDF/MDS)")
    p.add_argument("target", help="Zieldatei auf dem XP-Rechner, z. B. Ordner\\Spiel.iso")
    p.add_argument("--drive", help="CD-Laufwerk (Standard wie bei eject)")
    p.set_defaults(fn=cmd_ripiso)

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

    p = sub.add_parser("ls", help="Dateien mit Größe/Datum (statt run dir)")
    p.add_argument("path")
    p.add_argument("-r", "--recurse", type=int, nargs="?", const=-1, help="Tiefe, ohne Zahl = alles")
    p.add_argument("-p", "--pattern", nargs="+", help="Dateimuster, z.B. *.exe *.iso")
    p.add_argument("--files", action="store_true", help="nur Dateien")
    p.add_argument("--limit", type=int, default=2000)
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
    p.set_defaults(fn=cmd_disc)

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

    p = sub.add_parser("update", help="xpserver.py auf dem XP ersetzen und neu starten")
    p.add_argument("--file", help="andere Server-Datei (Standard: xpserver.py neben xpctl.py)")
    p.add_argument("--force", action="store_true", help="auch senden, wenn die Prüfsumme gleich ist")
    p.set_defaults(fn=cmd_update)

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
