#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""xpserver: sichtbarer Befehls-Server für den Retro-XP-Rechner.

Läuft mit Python 3.4 (letzte Version für Windows XP), nur Standardbibliothek.
Wird von Hand gestartet (xpserver.bat), Konsolenfenster bleibt offen und loggt
jeden Befehl. Kein Autostart, kein Dienst. Nur Clients aus dem erlaubten
Netz (Konfiguration "allow", --allow oder XPCTL_ALLOW), Anmeldung per HMAC-Challenge mit Passwort.
Der Verkehr selbst ist nicht verschlüsselt, nur im vertrauenswürdigen LAN betreiben.

Ops: ping, run, put, get, update, ui, shot, click, press, settext, key, type, focus, close,
updates, programs, sysinfo (Inventar über winreg/ctypes),
reg, settings, productkey (Einstellungen auslesen, nur lesen),
lnk, desk, deskpos, mount, eject, vol, ripiso, pesec (Spiele-Werkzeuge: Verknüpfungen, Desktop, Laufwerke),
driver (Geräteliste, INF-Treiber installieren), games (Versionen der installierten Spiele),
ls, cat, ps, kill, hash, copy, rm, disc, setupapi (statt run mit dir/findstr/type/tasklist/taskkill/xcopy/del).
"""
from __future__ import print_function

import argparse
import binascii
import getpass
import hashlib
import hmac
import ipaddress
import json
import os
import platform
import re
import socket
import struct
import subprocess
import sys
import threading
import zlib
import time

VERSION = 9
RESTART_CODE = 42
MAX_FRAME = 16 * 1024 * 1024
CHUNK = 256 * 1024
DEFAULT_PORT = 5050
CONFIG_NAME = "xpserver.json"
CFG = {}   # Inhalt der Konfigurationsdatei, siehe xpserver.example.json

SELF = os.path.abspath(__file__)
HERE = os.path.dirname(SELF)
LOGFILE = os.path.join(HERE, "xpserver.log")
_log_lock = threading.Lock()


def load_config(path):
    """Liest die JSON-Konfiguration. Fehlt die Datei, bleibt CFG leer (Funktionen mit Pfaden melden dann 'nicht konfiguriert')."""
    CFG.clear()
    if path and os.path.isfile(path):
        with open(path, "rb") as f:
            CFG.update(json.loads(f.read().decode("utf-8")))


def cfg_need(key, what):
    v = CFG.get(key)
    if not v:
        raise ValueError('nicht konfiguriert: "%s" (%s) in %s' % (key, what, CONFIG_NAME))
    return v


def sysroot():
    r = os.environ.get("SystemRoot") or os.environ.get("windir")
    if not r:
        raise ValueError("Windows-Ordner unbekannt (SystemRoot fehlt)")
    return r


def launcher_name():
    s = CFG.get("launcher_script")
    return os.path.basename(s).lower() if s else ""


# ---------- Log ----------

def log(msg):
    line = "%s  %s" % (time.strftime("%Y-%m-%d %H:%M:%S"), msg)
    with _log_lock:
        enc = sys.stdout.encoding or "ascii"
        print(line.encode(enc, "replace").decode(enc))
        sys.stdout.flush()
        try:
            with open(LOGFILE, "ab") as f:
                f.write((line + "\n").encode("utf-8"))
        except Exception:
            pass


# ---------- Framing ----------

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
        raise ValueError("Frame zu groß: %d" % n)
    return recv_exact(sock, n)


def send_json(sock, obj):
    send_frame(sock, json.dumps(obj).encode("utf-8"))


def recv_json(sock):
    return json.loads(recv_frame(sock).decode("utf-8"))


# ---------- Auth ----------

def mac(key, role, nonce):
    msg = role + b"|" + nonce.encode("ascii")
    return hmac.new(key, msg, hashlib.sha256).hexdigest()


def handshake(sock, key):
    nonce = binascii.hexlify(os.urandom(16)).decode("ascii")
    send_json(sock, {"hello": "xpserver", "version": VERSION, "nonce": nonce})
    resp = recv_json(sock)
    ok = hmac.compare_digest(
        str(resp.get("auth", "")).encode("ascii", "replace"),
        mac(key, b"client", nonce).encode("ascii"),
    )
    if not ok:
        time.sleep(1)
        send_json(sock, {"ok": False, "error": "auth"})
        return False
    cnonce = str(resp.get("cnonce", ""))
    send_json(sock, {"ok": True, "auth": mac(key, b"server", cnonce)})
    return True


# ---------- Ops ----------

def oem_encoding():
    if os.name == "nt":
        try:
            import ctypes
            return "cp%d" % ctypes.windll.kernel32.GetOEMCP()
        except Exception:
            pass
    return "utf-8"


def kill_tree(pid):
    if os.name == "nt":
        subprocess.call(["taskkill", "/F", "/T", "/PID", str(pid)],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    else:
        try:
            os.kill(pid, 9)
        except OSError:
            pass


def self_sha256():
    with open(SELF, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()


def op_ping(sock, req):
    return {
        "ok": True,
        "version": VERSION,
        "sha256": self_sha256(),
        "supervised": bool(os.environ.get("XPCTL_SUPERVISED")),
        "script": SELF,
        "host": socket.gethostname(),
        "os": platform.platform(),
        "python": sys.version.split()[0],
        "time": time.strftime("%Y-%m-%d %H:%M:%S"),
        "cwd": os.getcwd(),
    }


def op_run(sock, req):
    cmd = req["cmd"]
    cwd = req.get("cwd") or None
    timeout = float(req.get("timeout", 60))
    if req.get("detach"):
        p = subprocess.Popen(cmd, shell=True, cwd=cwd, stdin=subprocess.DEVNULL,
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        return {"ok": True, "detached": True, "pid": p.pid}
    p = subprocess.Popen(cmd, shell=True, cwd=cwd, stdin=subprocess.DEVNULL,
                         stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    timed_out = False
    try:
        out, err = p.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        timed_out = True
        kill_tree(p.pid)
        out, err = p.communicate()
    enc = oem_encoding()
    return {
        "ok": True,
        "exit": p.returncode,
        "timed_out": timed_out,
        "stdout": out.decode(enc, "replace"),
        "stderr": err.decode(enc, "replace"),
    }


def op_put(sock, req):
    path = req["path"]
    size = int(req["size"])
    name = req.get("name") or "datei"
    if path.endswith(("\\", "/")) or os.path.isdir(path):
        path = os.path.join(path, name)
    parent = os.path.dirname(path)
    if parent and not os.path.isdir(parent):
        if req.get("mkdir"):
            os.makedirs(parent)
        else:
            return {"ok": False, "error": "Zielordner fehlt: %s" % parent}
    send_json(sock, {"ok": True, "ready": True, "target": path})
    part = path + ".part"
    h = hashlib.sha256()
    got = 0
    with open(part, "wb") as f:
        while got < size:
            data = recv_frame(sock)
            f.write(data)
            h.update(data)
            got += len(data)
    if os.path.exists(path):
        os.remove(path)
    os.rename(part, path)
    return {"ok": True, "size": got, "sha256": h.hexdigest(), "target": path}


def op_get(sock, req):
    path = req["path"]
    if not os.path.isfile(path):
        return {"ok": False, "error": "keine Datei: %s" % path}
    size = os.path.getsize(path)
    send_json(sock, {"ok": True, "ready": True, "size": size})
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while True:
            data = f.read(CHUNK)
            if not data:
                break
            h.update(data)
            send_frame(sock, data)
    return {"ok": True, "size": size, "sha256": h.hexdigest()}


def op_update(sock, req):
    """Ersetzt xpserver.py durch die gesendete Version und startet neu.

    Nur unter xpserver.bat (Exit-Code 42 = Neustart). Die alte Version bleibt
    als xpserver.py.bak liegen, bis die neue erfolgreich lauscht; scheitert der
    Start, spielt die .bat sie zurück.
    """
    if not os.environ.get("XPCTL_SUPERVISED"):
        return {"ok": False, "error": "nicht über xpserver.bat gestartet, kein Neustart möglich"}
    size = int(req["size"])
    want = req["sha256"]
    send_json(sock, {"ok": True, "ready": True})
    data = b""
    while len(data) < size:
        data += recv_frame(sock)
    if hashlib.sha256(data).hexdigest() != want:
        return {"ok": False, "error": "Prüfsumme stimmt nicht"}
    try:
        compile(data, "xpserver.py (neu)", "exec")
    except SyntaxError as e:
        return {"ok": False, "error": "Syntaxfehler in der neuen Version: %s" % e}
    new, bak = SELF + ".new", SELF + ".bak"
    with open(new, "wb") as f:
        f.write(data)
    if os.path.exists(bak):
        os.remove(bak)
    os.rename(SELF, bak)
    os.rename(new, SELF)
    log("  Update eingespielt (%d Bytes), starte neu" % len(data))

    def restart():
        time.sleep(0.5)
        os._exit(RESTART_CODE)
    threading.Thread(target=restart).start()
    return {"ok": True, "restarting": True, "sha256": want}


# ---------- Windows-UI (ctypes, erst beim ersten Aufruf geladen) ----------
#
# ui    : Fenster + Controls mit Positionen als Text (billig, zuerst benutzen)
# shot  : Screenshot (komprimiert BGR, der Client macht ein PNG daraus)
# click, press, settext, type, key, focus, close: Eingaben

_W = []

VK_NAMES = {
    "ENTER": 0x0D, "RETURN": 0x0D, "TAB": 0x09, "ESC": 0x1B, "ESCAPE": 0x1B,
    "SPACE": 0x20, "BACKSPACE": 0x08, "DELETE": 0x2E, "DEL": 0x2E, "INSERT": 0x2D,
    "HOME": 0x24, "END": 0x23, "PGUP": 0x21, "PGDN": 0x22,
    "LEFT": 0x25, "UP": 0x26, "RIGHT": 0x27, "DOWN": 0x28,
}
for _i in range(1, 13):
    VK_NAMES["F%d" % _i] = 0x6F + _i
VK_MODS = {"CTRL": 0x11, "CONTROL": 0x11, "ALT": 0x12, "SHIFT": 0x10, "WIN": 0x5B}
VK_EXT = (0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E)


def win():
    """Lädt user32/gdi32 beim ersten Aufruf und setzt alle Signaturen."""
    if _W:
        return _W[0]
    if os.name != "nt":
        raise RuntimeError("nur unter Windows")
    import ctypes
    import types
    from ctypes import wintypes
    ns = types.SimpleNamespace()
    ns.ct = ctypes
    ns.wt = wintypes
    u = ns.u = ctypes.windll.user32
    g = ns.g = ctypes.windll.gdi32
    k = ns.k = ctypes.windll.kernel32
    ns.psapi = ctypes.windll.psapi
    V, I, U, D = ctypes.c_void_p, ctypes.c_int, ctypes.c_uint, wintypes.DWORD
    ns.ENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, V, ctypes.c_long)

    def sig(fn, res, *args):
        fn.restype = res
        fn.argtypes = list(args)

    sig(u.EnumWindows, wintypes.BOOL, ns.ENUMPROC, ctypes.c_long)
    sig(u.EnumChildWindows, wintypes.BOOL, V, ns.ENUMPROC, ctypes.c_long)
    sig(u.GetWindowRect, wintypes.BOOL, V, ctypes.POINTER(wintypes.RECT))
    sig(u.GetClassNameW, I, V, ctypes.c_wchar_p, I)
    sig(u.GetWindowThreadProcessId, D, V, ctypes.POINTER(D))
    sig(u.GetWindowLongW, ctypes.c_long, V, I)
    sig(u.IsWindow, wintypes.BOOL, V)
    sig(u.IsWindowVisible, wintypes.BOOL, V)
    sig(u.IsWindowEnabled, wintypes.BOOL, V)
    sig(u.IsIconic, wintypes.BOOL, V)
    sig(u.IsHungAppWindow, wintypes.BOOL, V)
    sig(u.GetParent, V, V)
    sig(u.GetWindow, V, V, U)
    sig(u.GetDlgCtrlID, I, V)
    sig(u.GetForegroundWindow, V)
    sig(u.GetSystemMetrics, I, I)
    sig(u.SendMessageTimeoutW, ctypes.c_size_t, V, U, ctypes.c_size_t, V, U, U,
        ctypes.POINTER(ctypes.c_size_t))
    sig(u.PostMessageW, wintypes.BOOL, V, U, ctypes.c_size_t, V)
    sig(u.SetCursorPos, wintypes.BOOL, I, I)
    sig(u.mouse_event, None, D, D, D, D, ctypes.c_size_t)
    sig(u.keybd_event, None, ctypes.c_ubyte, ctypes.c_ubyte, D, ctypes.c_size_t)
    sig(u.MapVirtualKeyW, U, U, U)
    sig(u.VkKeyScanW, ctypes.c_short, ctypes.c_wchar)
    sig(u.ShowWindow, wintypes.BOOL, V, I)
    sig(u.BringWindowToTop, wintypes.BOOL, V)
    sig(u.SetForegroundWindow, wintypes.BOOL, V)
    sig(u.AttachThreadInput, wintypes.BOOL, D, D, wintypes.BOOL)
    sig(u.GetDC, V, V)
    sig(u.ReleaseDC, I, V, V)
    sig(k.GetCurrentThreadId, D)
    sig(k.OpenProcess, V, D, wintypes.BOOL, D)
    sig(k.CloseHandle, wintypes.BOOL, V)
    sig(ns.psapi.GetModuleBaseNameW, D, V, V, ctypes.c_wchar_p, D)
    sig(g.CreateCompatibleDC, V, V)
    sig(g.CreateCompatibleBitmap, V, V, I, I)
    sig(g.SelectObject, V, V, V)
    sig(g.BitBlt, wintypes.BOOL, V, I, I, I, I, V, I, I, D)
    sig(g.StretchBlt, wintypes.BOOL, V, I, I, I, I, V, I, I, I, I, D)
    sig(g.SetStretchBltMode, I, V, I)
    sig(g.DeleteObject, wintypes.BOOL, V)
    sig(g.DeleteDC, wintypes.BOOL, V)

    class BITMAPINFOHEADER(ctypes.Structure):
        _fields_ = [("biSize", D), ("biWidth", ctypes.c_long), ("biHeight", ctypes.c_long),
                    ("biPlanes", wintypes.WORD), ("biBitCount", wintypes.WORD),
                    ("biCompression", D), ("biSizeImage", D),
                    ("biXPelsPerMeter", ctypes.c_long), ("biYPelsPerMeter", ctypes.c_long),
                    ("biClrUsed", D), ("biClrImportant", D)]
    ns.BIH = BITMAPINFOHEADER
    sig(g.GetDIBits, I, V, V, U, U, V, ctypes.POINTER(BITMAPINFOHEADER), U)
    _W.append(ns)
    return ns


def w_send(w, hwnd, msg, wparam=0, lparam=0, timeout=300):
    """SendMessage mit Timeout, hängt nicht an hängenden Fenstern. Gibt (ok, Ergebnis)."""
    res = w.ct.c_size_t(0)
    ok = w.u.SendMessageTimeoutW(hwnd, msg, wparam, lparam, 0x0003, timeout, w.ct.byref(res))
    return bool(ok), res.value


def w_text(w, hwnd, maxlen=400):
    buf = w.ct.create_unicode_buffer(maxlen)
    ok, _ = w_send(w, hwnd, 0x000D, maxlen, w.ct.addressof(buf))  # WM_GETTEXT
    return buf.value if ok else ""


def w_exe(w, pid):
    h = w.k.OpenProcess(0x0410, False, pid)
    if not h:
        return ""
    try:
        buf = w.ct.create_unicode_buffer(260)
        w.psapi.GetModuleBaseNameW(h, None, buf, 260)
        return buf.value
    finally:
        w.k.CloseHandle(h)


def w_info(w, hwnd, top=False):
    cls = w.ct.create_unicode_buffer(256)
    w.u.GetClassNameW(hwnd, cls, 256)
    r = w.wt.RECT()
    w.u.GetWindowRect(hwnd, w.ct.byref(r))
    d = {
        "hwnd": hwnd, "class": cls.value, "text": w_text(w, hwnd),
        "rect": [r.left, r.top, r.right, r.bottom],
        "visible": bool(w.u.IsWindowVisible(hwnd)),
        "enabled": bool(w.u.IsWindowEnabled(hwnd)),
    }
    if top:
        pid = w.wt.DWORD(0)
        w.u.GetWindowThreadProcessId(hwnd, w.ct.byref(pid))
        d["pid"] = pid.value
        d["exe"] = w_exe(w, pid.value)
        d["hung"] = bool(w.u.IsHungAppWindow(hwnd))
        d["minimized"] = bool(w.u.IsIconic(hwnd))
        d["owner"] = w.u.GetWindow(hwnd, 4) or 0
    else:
        d["id"] = w.u.GetDlgCtrlID(hwnd)
        if cls.value.lower() == "button":
            bstyle = w.u.GetWindowLongW(hwnd, -16) & 0xF
            if bstyle in (2, 3, 4, 5, 6, 9):  # Checkbox/Radio
                ok, state = w_send(w, hwnd, 0x00F0)  # BM_GETCHECK
                if ok:
                    d["checked"] = state
    return d


def w_enum(w, parent=None):
    out = []

    def cb(hwnd, lparam):
        if hwnd:
            out.append(hwnd)
        return True
    fn = w.ENUMPROC(cb)
    if parent is None:
        w.u.EnumWindows(fn, 0)
    else:
        w.u.EnumChildWindows(parent, fn, 0)
    return out


def op_ui(sock, req):
    w = win()
    fg = w.u.GetForegroundWindow() or 0
    target = int(req.get("hwnd") or fg)
    show_all = bool(req.get("all"))
    wins = []
    for h in w_enum(w):
        if not w.u.IsWindowVisible(h):
            continue
        info = w_info(w, h, True)
        if info["text"] or show_all:
            wins.append(info)
    controls = []
    if target and w.u.IsWindow(target):
        for h in w_enum(w, target)[:600]:
            c = w_info(w, h)
            c["parent"] = w.u.GetParent(h) or 0
            if c["visible"] or show_all:
                controls.append(c)
    return {
        "ok": True,
        "screen": [w.u.GetSystemMetrics(0), w.u.GetSystemMetrics(1)],
        "foreground": fg, "target": target, "windows": wins, "controls": controls,
    }


def op_shot(sock, req):
    w = win()
    sw, sh = w.u.GetSystemMetrics(0), w.u.GetSystemMetrics(1)
    if req.get("hwnd"):
        r = w.wt.RECT()
        w.u.GetWindowRect(int(req["hwnd"]), w.ct.byref(r))
        l, t, rr, b = r.left, r.top, r.right, r.bottom
    elif req.get("region"):
        l, t, rr, b = [int(v) for v in req["region"]]
    else:
        l, t, rr, b = 0, 0, sw, sh
    l, t, rr, b = max(l, 0), max(t, 0), min(rr, sw), min(b, sh)
    cw, ch = rr - l, b - t
    if cw < 1 or ch < 1:
        return {"ok": False, "error": "leerer Bereich"}
    scale = float(req.get("scale", 1.0))
    scale = min(max(scale, 0.1), 1.0)
    ow, oh = max(1, int(cw * scale)), max(1, int(ch * scale))
    stride = ((ow * 3 + 3) // 4) * 4
    buf = w.ct.create_string_buffer(stride * oh)
    sdc = w.u.GetDC(None)
    mem = bmp = None
    try:
        mem = w.g.CreateCompatibleDC(sdc)
        bmp = w.g.CreateCompatibleBitmap(sdc, ow, oh)
        old = w.g.SelectObject(mem, bmp)
        if scale >= 1.0:
            ok = w.g.BitBlt(mem, 0, 0, cw, ch, sdc, l, t, 0x40CC0020)  # SRCCOPY|CAPTUREBLT
        else:
            w.g.SetStretchBltMode(mem, 4)  # HALFTONE
            ok = w.g.StretchBlt(mem, 0, 0, ow, oh, sdc, l, t, cw, ch, 0x40CC0020)
        w.g.SelectObject(mem, old)
        if not ok:
            return {"ok": False, "error": "BitBlt fehlgeschlagen"}
        bih = w.BIH()
        bih.biSize = w.ct.sizeof(w.BIH)
        bih.biWidth = ow
        bih.biHeight = -oh  # top-down
        bih.biPlanes = 1
        bih.biBitCount = 24
        lines = w.g.GetDIBits(sdc, bmp, 0, oh, buf, w.ct.byref(bih), 0)
        if lines != oh:
            return {"ok": False, "error": "GetDIBits lieferte %d von %d Zeilen" % (lines, oh)}
    finally:
        if bmp:
            w.g.DeleteObject(bmp)
        if mem:
            w.g.DeleteDC(mem)
        w.u.ReleaseDC(None, sdc)
    payload = zlib.compress(buf.raw, 1)
    send_json(sock, {"ok": True, "ready": True, "width": ow, "height": oh, "stride": stride,
                     "rect": [l, t, rr, b], "scale": scale, "size": len(payload)})
    h = hashlib.sha256(payload)
    for i in range(0, len(payload), CHUNK):
        send_frame(sock, payload[i:i + CHUNK])
    return {"ok": True, "sha256": h.hexdigest()}


def op_click(sock, req):
    w = win()
    x, y = int(req["x"]), int(req["y"])
    w.u.SetCursorPos(x, y)
    time.sleep(0.05)
    if req.get("move_only"):
        return {"ok": True}
    down, up = {"left": (2, 4), "right": (8, 0x10), "middle": (0x20, 0x40)}[req.get("button", "left")]
    for _ in range(2 if req.get("double") else 1):
        w.u.mouse_event(down, 0, 0, 0, 0)
        time.sleep(0.03)
        w.u.mouse_event(up, 0, 0, 0, 0)
        time.sleep(0.06)
    return {"ok": True}


def op_press(sock, req):
    """Button auslösen ohne Koordinaten (Pushbutton: WM_COMMAND, sonst BM_CLICK)."""
    w = win()
    h = int(req["hwnd"])
    if not w.u.IsWindow(h):
        return {"ok": False, "error": "Fenster existiert nicht"}
    if not w.u.IsWindowEnabled(h):
        return {"ok": False, "error": "Control ist deaktiviert"}
    parent = w.u.GetParent(h)
    bstyle = w.u.GetWindowLongW(h, -16) & 0xF
    if parent and bstyle in (0, 1):
        w.u.PostMessageW(parent, 0x0111, w.u.GetDlgCtrlID(h) & 0xFFFF, h)  # WM_COMMAND, BN_CLICKED
        how = "WM_COMMAND"
    else:
        w.u.PostMessageW(h, 0x00F5, 0, None)  # BM_CLICK
        how = "BM_CLICK"
    return {"ok": True, "how": how}


def op_settext(sock, req):
    w = win()
    buf = w.ct.create_unicode_buffer(req["text"])
    ok, _ = w_send(w, int(req["hwnd"]), 0x000C, 0, w.ct.addressof(buf), 1000)  # WM_SETTEXT
    return {"ok": ok} if ok else {"ok": False, "error": "WM_SETTEXT ohne Antwort (Fenster hängt?)"}


def vk_for(w, name):
    n = name.upper()
    if n in VK_NAMES:
        return VK_NAMES[n], 0
    if len(name) == 1:
        r = w.u.VkKeyScanW(name)
        if r == -1:
            raise ValueError("Zeichen nicht auf dem Tastaturlayout: %r" % name)
        return r & 0xFF, (r >> 8) & 0xFF
    raise ValueError("unbekannte Taste: %s" % name)


def key_ev(w, vk, up=False):
    flags = (1 if vk in VK_EXT else 0) | (2 if up else 0)
    w.u.keybd_event(vk, w.u.MapVirtualKeyW(vk, 0) & 0xFF, flags, 0)


def key_tap(w, vk, mods):
    for m in mods:
        key_ev(w, m)
    key_ev(w, vk)
    time.sleep(0.01)
    key_ev(w, vk, True)
    for m in reversed(mods):
        key_ev(w, m, True)
    time.sleep(0.02)


def op_key(sock, req):
    w = win()
    for combo in req["keys"]:
        parts = combo.split("+") if len(combo) > 1 else [combo]
        if combo.endswith("++"):
            parts = combo[:-2].split("+") + ["+"]
        mods = []
        for p in parts[:-1]:
            if p.upper() not in VK_MODS:
                raise ValueError("unbekannter Modifier: %s" % p)
            mods.append(VK_MODS[p.upper()])
        vk, _ = vk_for(w, parts[-1])
        key_tap(w, vk, mods)
    return {"ok": True}


def op_type(sock, req):
    w = win()
    for ch in req["text"]:
        if ch == "\n":
            key_tap(w, 0x0D, [])
            continue
        if ch == "\t":
            key_tap(w, 0x09, [])
            continue
        vk, st = vk_for(w, ch)
        mods = []
        if st & 1:
            mods.append(0x10)
        if st & 2:
            mods.append(0x11)
        if st & 4:
            mods.append(0x12)
        key_tap(w, vk, mods)
    return {"ok": True}


def op_focus(sock, req):
    w = win()
    h = int(req["hwnd"])
    if not w.u.IsWindow(h):
        return {"ok": False, "error": "Fenster existiert nicht"}
    fg = w.u.GetForegroundWindow()
    fgt = w.u.GetWindowThreadProcessId(fg, None) if fg else 0
    cur = w.k.GetCurrentThreadId()
    if fgt and fgt != cur:
        w.u.AttachThreadInput(cur, fgt, True)
    try:
        if w.u.IsIconic(h):
            w.u.ShowWindow(h, 9)
        w.u.BringWindowToTop(h)
        w.u.SetForegroundWindow(h)
    finally:
        if fgt and fgt != cur:
            w.u.AttachThreadInput(cur, fgt, False)
    time.sleep(0.1)
    return {"ok": True, "foreground": w.u.GetForegroundWindow() == h}


def op_close(sock, req):
    w = win()
    w.u.PostMessageW(int(req["hwnd"]), 0x0010, 0, None)  # WM_CLOSE
    return {"ok": True}


# ---------- Inventar (winreg/ctypes, nichts wird installiert oder geändert) ----------
#
# updates : Hotfixes/Updates (Updates-Schlüssel + Uninstall), dedupliziert nach KB
# programs: Uninstall-Einträge, Updates/Systemkomponenten markiert (kind)
# sysinfo : Windows, CPU, RAM, Laufwerke, IE, DirectX, .NET

HKLM_UNINSTALL = r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
KB_RE = re.compile(r"\b(KB\d{5,7}|Q\d{6,7})\b", re.I)
UPDATE_NAME_RE = re.compile(
    r"^(security update|update for|hotfix for|critical update|update rollup|"
    r"windows xp (hotfix|service pack)|service pack)", re.I)


def _plain(v):
    if isinstance(v, (str, int)):
        return v
    if isinstance(v, list):
        return " ".join(str(x) for x in v)
    return None  # REG_BINARY u.ä. weglassen


def reg_read(path, want_subs=True, hive=None):
    """(Werte, Unterschlüssel, Zeitstempel der letzten Änderung) oder None. hive: Kürzel, Standard HKLM."""
    import winreg
    try:
        k = winreg.OpenKey(hive_handle(hive) if hive else winreg.HKEY_LOCAL_MACHINE, path, 0, winreg.KEY_READ)
    except OSError:
        return None
    try:
        nsubs, nvals, last = winreg.QueryInfoKey(k)
        vals = {}
        for i in range(nvals):
            try:
                name, data, _ = winreg.EnumValue(k, i)
            except OSError:
                break
            v = _plain(data)
            if v is not None:
                vals[name] = v
        subs = []
        if want_subs:
            for i in range(nsubs):
                try:
                    subs.append(winreg.EnumKey(k, i))
                except OSError:
                    break
        stamp = ""
        if last:
            try:
                stamp = time.strftime("%Y-%m-%d", time.gmtime(last / 10000000.0 - 11644473600))
            except (ValueError, OverflowError, OSError):
                stamp = ""
        return vals, subs, stamp
    finally:
        winreg.CloseKey(k)


def norm_date(s):
    """YYYYMMDD oder M/D/YYYY (so schreibt es der XP-Updater) -> YYYY-MM-DD."""
    s = str(s or "").strip()
    m = re.match(r"^(\d{4})(\d{2})(\d{2})$", s)
    if m:
        return "%s-%s-%s" % m.groups()
    m = re.match(r"^(\d{1,2})/(\d{1,2})/(\d{4})$", s)
    if m:
        return "%s-%02d-%02d" % (m.group(3), int(m.group(1)), int(m.group(2)))
    m = re.match(r"^(\d{4})-(\d{1,2})-(\d{1,2})$", s)
    if m:
        return "%s-%02d-%02d" % (m.group(1), int(m.group(2)), int(m.group(3)))
    return s


def kb_of(*texts):
    for t in texts:
        m = KB_RE.search(t or "")
        if m:
            return m.group(1).upper()
    return ""


def category_of(release, text):
    r = (release or "").strip()
    if r:
        return r
    t = (text or "").lower()
    if "security update" in t:
        return "Security Update"
    if "hotfix" in t:
        return "Hotfix"
    if "service pack" in t:
        return "Service Pack"
    if "rollup" in t:
        return "Update Rollup"
    return "Update"


def collect_updates():
    found = {}  # Schlüssel (KB oder Name) -> Eintrag

    def add(key, kb, title, product, category, date, source):
        e = found.get(key)
        if e is None:
            e = {"kb": kb, "title": "", "product": "", "category": "", "date": "", "sources": []}
            found[key] = e
        if title and (not e["title"] or len(title) > len(e["title"])):
            e["title"] = title
        for f, v in (("product", product), ("category", category), ("date", date)):
            if v and not e[f]:
                e[f] = v
        if source not in e["sources"]:
            e["sources"].append(source)

    # 1) HKLM\SOFTWARE\Microsoft\Updates\<Produkt>[\<SP>]\<KB>
    base = r"SOFTWARE\Microsoft\Updates"
    root = reg_read(base)
    for prod in (root[1] if root else []):
        stack = [(base + "\\" + prod, prod, 1)]
        while stack:
            path, label, depth = stack.pop()
            node = reg_read(path)
            if node is None:
                continue
            vals, subs, stamp = node
            for sub in subs:
                if sub.lower() == "filelist":
                    continue
                child = path + "\\" + sub
                kb = kb_of(sub)
                if kb:
                    c = reg_read(child, False)
                    if c is None:
                        continue
                    cv, _, cstamp = c
                    title = cv.get("Description") or cv.get("PackageName") or ""
                    date = norm_date(cv.get("InstalledDate")) or cstamp
                    add(kb, kb, title, label,
                        category_of(cv.get("ReleaseType") or cv.get("Type"), title), date, "Updates")
                elif depth < 3:
                    stack.append((child, label + " " + sub if depth == 1 else label, depth + 1))

    # 2) Uninstall-Einträge, die Updates sind
    u = reg_read(HKLM_UNINSTALL)
    for name in (u[1] if u else []):
        e = reg_read(HKLM_UNINSTALL + "\\" + name, False)
        if e is None:
            continue
        v, _, stamp = e
        if program_kind(name, v) != "update":
            continue
        disp = str(v.get("DisplayName") or "")
        kb = kb_of(name, disp)
        key = kb or ("name:" + (disp or name))
        add(key, kb, disp, str(v.get("ParentDisplayName") or ""),
            category_of(v.get("ReleaseType"), disp),
            norm_date(v.get("InstallDate")) or stamp, "Uninstall")
    return sorted(found.values(), key=lambda x: (x["date"], x["kb"], x["title"]))


def program_kind(name, v):
    """'update', 'system' oder '' (normales Programm)."""
    disp = str(v.get("DisplayName") or "")
    if v.get("ParentKeyName") or v.get("ReleaseType"):
        return "update"
    if re.match(r"^(KB|Q)\d{5,7}", name, re.I) or "hotfix" in name.lower():
        return "update"
    if UPDATE_NAME_RE.match(disp) or (KB_RE.search(disp) and
                                      re.search(r"\b(update|hotfix|patch)\b", disp, re.I)):
        return "update"
    if v.get("SystemComponent") == 1:
        return "system"
    return ""


def collect_programs():
    out = []
    u = reg_read(HKLM_UNINSTALL)
    for name in (u[1] if u else []):
        e = reg_read(HKLM_UNINSTALL + "\\" + name, False)
        if e is None:
            continue
        v, _, stamp = e
        disp = str(v.get("DisplayName") or "").strip()
        if not disp:
            continue
        out.append({
            "name": disp, "version": str(v.get("DisplayVersion") or ""),
            "publisher": str(v.get("Publisher") or ""),
            "date": norm_date(v.get("InstallDate")) or stamp,
            "kind": program_kind(name, v), "key": name,
            "location": str(v.get("InstallLocation") or ""),
        })
    out.sort(key=lambda x: x["name"].lower())
    return out


def winpatch_state():
    """Service-Pack-Stand und POSReady-Hack."""
    nt = reg_read(r"SOFTWARE\Microsoft\Windows NT\CurrentVersion", False)
    v = nt[0] if nt else {}
    sp_ctl = reg_read(r"SYSTEM\CurrentControlSet\Control\Windows", False)
    csd = (sp_ctl[0].get("CSDVersion") if sp_ctl else None)
    pos = reg_read(r"SYSTEM\WPA\PosReady", False)
    pos_state = None
    if pos is not None:
        pos_state = {k: val for k, val in pos[0].items()}
    return {
        "product": v.get("ProductName", ""), "version": v.get("CurrentVersion", ""),
        "build": v.get("CurrentBuildNumber", ""), "csd": v.get("CSDVersion", ""),
        "sp_level": (csd >> 8) if isinstance(csd, int) else None,
        "posready_key": pos is not None, "posready": pos_state,
    }


def op_updates(sock, req):
    ups = collect_updates()
    st = winpatch_state()
    return {"ok": True, "state": st, "updates": ups}


def op_programs(sock, req):
    return {"ok": True, "programs": collect_programs()}


DX_NAMES = {
    "4.02.0095": "1.0", "4.03.00.1096": "2.0", "4.04.0068": "3.0", "4.04.0069": "3.0",
    "4.05.00.0155": "5.0", "4.05.01.1998": "5.0", "4.06.02.0436": "6.0",
    "4.07.00.0700": "7.0", "4.07.00.0716": "7.0a", "4.08.00.0400": "8.0",
    "4.08.01.0881": "8.1", "4.08.01.0810": "8.1", "4.09.0000.0900": "9.0",
    "4.09.0000.0901": "9.0a", "4.09.0000.0902": "9.0b", "4.09.0000.0904": "9.0c",
    "4.09.00.0900": "9.0", "4.09.00.0901": "9.0a", "4.09.00.0902": "9.0b",
    "4.09.00.0904": "9.0c",
}


def op_sysinfo(sock, req):
    import ctypes
    from ctypes import wintypes
    k32 = ctypes.windll.kernel32
    out = {"ok": True}
    nt = reg_read(r"SOFTWARE\Microsoft\Windows NT\CurrentVersion", False)
    nv = nt[0] if nt else {}
    st = winpatch_state()
    out["windows"] = {
        "product": st["product"], "version": st["version"], "build": st["build"],
        "sp": st["csd"], "owner": nv.get("RegisteredOwner", ""),
        "org": nv.get("RegisteredOrganization", ""),
        "installed": (time.strftime("%Y-%m-%d", time.localtime(nv["InstallDate"]))
                      if isinstance(nv.get("InstallDate"), int) else ""),
        "posready": bool(st["posready_key"]),
    }
    out["host"] = os.environ.get("COMPUTERNAME") or socket.gethostname()
    # Sprache
    try:
        k32.GetLocaleInfoW.argtypes = [wintypes.DWORD, wintypes.DWORD, ctypes.c_wchar_p, ctypes.c_int]
        sysl = k32.GetSystemDefaultLCID()
        uil = ctypes.windll.kernel32.GetUserDefaultUILanguage()
        buf = ctypes.create_unicode_buffer(128)
        k32.GetLocaleInfoW(sysl, 0x1001, buf, 128)  # LOCALE_SENGLANGUAGE
        name = buf.value
        k32.GetLocaleInfoW(uil, 0x1001, buf, 128)
        out["language"] = {"system": name, "system_lcid": "0x%04x" % (sysl & 0xFFFF),
                           "ui": buf.value, "ui_lcid": "0x%04x" % uil}
    except Exception as e:
        out["language"] = {"error": str(e)}
    # CPU
    cpu0 = reg_read(r"HARDWARE\DESCRIPTION\System\CentralProcessor\0", False)
    cpus = reg_read(r"HARDWARE\DESCRIPTION\System\CentralProcessor")
    out["cpu"] = {
        "name": " ".join(str(cpu0[0].get("ProcessorNameString", "")).split()) if cpu0 else "",
        "mhz": cpu0[0].get("~MHz") if cpu0 else None,
        "ident": cpu0[0].get("Identifier", "") if cpu0 else "",
        "cores": len(cpus[1]) if cpus else None,
    }
    # RAM
    class MEMSTAT(ctypes.Structure):
        _fields_ = [("dwLength", wintypes.DWORD), ("dwMemoryLoad", wintypes.DWORD),
                    ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
                    ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong),
                    ("ullTotalVirtual", ctypes.c_ulonglong), ("ullAvailVirtual", ctypes.c_ulonglong),
                    ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]
    ms = MEMSTAT()
    ms.dwLength = ctypes.sizeof(MEMSTAT)
    if k32.GlobalMemoryStatusEx(ctypes.byref(ms)):
        out["ram"] = {"total_mb": ms.ullTotalPhys // (1024 * 1024),
                      "free_mb": ms.ullAvailPhys // (1024 * 1024),
                      "load_pct": ms.dwMemoryLoad}
    # Laufwerke
    drives = []
    mask = k32.GetLogicalDrives()
    kinds = {2: "Wechsel", 3: "Fest", 4: "Netz", 5: "CD/DVD", 6: "RAM"}
    k32.GetDriveTypeW.argtypes = [ctypes.c_wchar_p]
    k32.GetDiskFreeSpaceExW.argtypes = [ctypes.c_wchar_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
    for i in range(26):
        if not mask & (1 << i):
            continue
        root = "%s:\\" % chr(65 + i)
        t = k32.GetDriveTypeW(root)
        d = {"drive": root, "type": kinds.get(t, str(t))}
        if t in (2, 3, 4):
            free, total = ctypes.c_ulonglong(0), ctypes.c_ulonglong(0)
            avail = ctypes.c_ulonglong(0)
            old = k32.SetErrorMode(1)  # kein "Laufwerk nicht bereit"-Dialog
            try:
                ok = k32.GetDiskFreeSpaceExW(root, ctypes.byref(avail), ctypes.byref(total), ctypes.byref(free))
            finally:
                k32.SetErrorMode(old)
            if ok:
                d["total_mb"] = total.value // (1024 * 1024)
                d["free_mb"] = free.value // (1024 * 1024)
        drives.append(d)
    out["drives"] = drives
    # IE
    ie = reg_read(r"SOFTWARE\Microsoft\Internet Explorer", False)
    iev = ie[0] if ie else {}
    out["ie"] = str(iev.get("svcVersion") or iev.get("Version") or "")
    # DirectX
    dx = reg_read(r"SOFTWARE\Microsoft\DirectX", False)
    dxr = str(dx[0].get("Version", "")) if dx else ""
    out["directx"] = {"raw": dxr, "name": DX_NAMES.get(dxr, "")}
    # .NET
    net = []
    ndp = reg_read(r"SOFTWARE\Microsoft\NET Framework Setup\NDP")
    for sub in (ndp[1] if ndp else []):
        if not sub.lower().startswith("v"):
            continue
        base = r"SOFTWARE\Microsoft\NET Framework Setup\NDP\%s" % sub
        cands = [(sub, base)]
        node = reg_read(base)
        for c in (node[1] if node else []):
            if c in ("Client", "Full"):
                cands.append(("%s %s" % (sub, c), base + "\\" + c))
        for label, path in cands:
            r = reg_read(path, False)
            if r and r[0].get("Install") == 1:
                net.append({"name": label, "version": str(r[0].get("Version", "")),
                            "sp": r[0].get("SP")})
    out["dotnet"] = net
    return out


# ---------- Einstellungen auslesen: reg, settings, productkey (nur lesen) ----------
#
# reg       : beliebigen Registry-Schlüssel lesen (HKLM/HKCU/HKU/HKCR/HKCC), optional rekursiv
# settings  : kuratierter Überblick (Explorer, Design, Region, Konten, Ruhe, Netz, Spiele-Tuning)
# productkey: DigitalProductId dekodieren (der Client schreibt ihn nur in eine Datei)

REG_TYPES = {0: "REG_NONE", 1: "REG_SZ", 2: "REG_EXPAND_SZ", 3: "REG_BINARY", 4: "REG_DWORD",
             5: "REG_DWORD_BE", 6: "REG_LINK", 7: "REG_MULTI_SZ", 8: "REG_RESOURCE_LIST",
             11: "REG_QWORD"}
HKLM_S = "HKLM"
HKCU_S = "HKCU"
EXPL = r"Software\Microsoft\Windows\CurrentVersion\Explorer"
FWP = r"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy"
SVCS = r"SYSTEM\CurrentControlSet\Services"
CLASS_VIDEO = r"SYSTEM\CurrentControlSet\Control\Class\{4D36E968-E325-11CE-BFC1-08002BE10318}"
CLASS_MEDIA = r"SYSTEM\CurrentControlSet\Control\Class\{4D36E96C-E325-11CE-BFC1-08002BE10318}"


def hive_handle(name):
    import winreg
    table = {"HKLM": winreg.HKEY_LOCAL_MACHINE, "HKEY_LOCAL_MACHINE": winreg.HKEY_LOCAL_MACHINE,
             "HKCU": winreg.HKEY_CURRENT_USER, "HKEY_CURRENT_USER": winreg.HKEY_CURRENT_USER,
             "HKU": winreg.HKEY_USERS, "HKEY_USERS": winreg.HKEY_USERS,
             "HKCR": winreg.HKEY_CLASSES_ROOT, "HKEY_CLASSES_ROOT": winreg.HKEY_CLASSES_ROOT,
             "HKCC": winreg.HKEY_CURRENT_CONFIG, "HKEY_CURRENT_CONFIG": winreg.HKEY_CURRENT_CONFIG}
    h = table.get(str(name).upper())
    if h is None:
        raise ValueError("unbekannter Hive: %s (HKLM, HKCU, HKU, HKCR, HKCC)" % name)
    return h


def split_regpath(s):
    s = s.strip().replace("/", "\\").strip("\\")
    head, _, rest = s.partition("\\")
    return head, rest


def show_data(data, limit=64):
    """Registry-Daten JSON-tauglich: Binärdaten als Hex (gekürzt)."""
    if isinstance(data, (bytes, bytearray)):
        h = binascii.hexlify(bytes(data[:limit])).decode("ascii")
        h = " ".join(h[i:i + 2] for i in range(0, len(h), 2))
        return h + (" ... (%d Bytes)" % len(data) if len(data) > limit else "")
    return data


def reg_tree(hive, path, depth, with_values, budget, top=True):
    import winreg
    node = {"path": path}
    try:
        k = winreg.OpenKey(hive_handle(hive), path, 0, winreg.KEY_READ)
    except (OSError, ValueError) as e:
        node["error"] = str(e)
        return node
    budget[0] -= 1
    try:
        nsubs, nvals, last = winreg.QueryInfoKey(k)
        vals = []
        if top or with_values:
            for i in range(nvals):
                try:
                    name, data, typ = winreg.EnumValue(k, i)
                except OSError:
                    break
                vals.append({"name": name, "type": REG_TYPES.get(typ, str(typ)),
                             "data": show_data(data)})
        node["nvalues"] = nvals
        subs = []
        for i in range(nsubs):
            try:
                subs.append(winreg.EnumKey(k, i))
            except OSError:
                break
    finally:
        winreg.CloseKey(k)
    node["values"] = vals
    node["subkeys"] = subs
    if depth > 0:
        node["children"] = []
        for s in subs:
            if budget[0] <= 0:
                node["truncated"] = True
                break
            node["children"].append(reg_tree(hive, path + "\\" + s if path else s,
                                             depth - 1, with_values, budget, False))
    return node


def op_reg(sock, req):
    hive, path = split_regpath(req["key"])
    hive_handle(hive)
    budget = [int(req.get("limit", 3000))]
    node = reg_tree(hive, path, min(int(req.get("recurse", 0)), 8), bool(req.get("values")), budget)
    if "error" in node:
        return {"ok": False, "error": "%s\\%s: %s" % (hive, path, node["error"])}
    return {"ok": True, "hive": hive.upper(), "tree": node}


def rget(hive, path, name, default=None):
    import winreg
    try:
        k = winreg.OpenKey(hive_handle(hive), path, 0, winreg.KEY_READ)
    except OSError:
        return default
    try:
        data, typ = winreg.QueryValueEx(k, name)
    except OSError:
        return default
    finally:
        winreg.CloseKey(k)
    return show_data(data)


def rsubs(hive, path):
    r = reg_read(path, True, hive)
    return r[1] if r else []


def rvals(hive, path):
    r = reg_read(path, False, hive)
    return r[0] if r else {}


class Sec(object):
    def __init__(self, title):
        self.title = title
        self.items = []

    def add(self, k, v, reg="", note=""):
        self.items.append({"k": k, "v": v, "reg": reg, "note": note})

    def get(self, k, hive, path, name, decode=None):
        v = rget(hive, path, name)
        note = ""
        if decode and v is not None and not isinstance(v, list):
            note = decode.get(v, "")
        self.add(k, v, "%s\\%s\\%s" % (hive, path, name if name else "(Standard)"), note)
        return v


def sh(cmd, timeout=20):
    try:
        p = subprocess.Popen(cmd, shell=True, stdin=subprocess.DEVNULL,
                             stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        try:
            out, _ = p.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            kill_tree(p.pid)
            out, _ = p.communicate()
        return out.decode(oem_encoding(), "replace")
    except Exception:
        return ""


def net_names(out, columns=0):
    names, on = [], False
    for ln in out.splitlines():
        if ln.startswith("-----"):
            on = True
            continue
        if not on:
            continue
        low = ln.lower()
        if low.startswith("the command") or low.startswith("der befehl"):
            break
        if not ln.strip():
            continue
        if columns:
            names.extend(x for x in (ln[i:i + columns].strip() for i in range(0, len(ln), columns)) if x)
        else:
            names.append(ln.strip())
    return names


def display_modes():
    """Aktuelle und in der Registry gespeicherte Auflösung/Farbtiefe/Hz (EnumDisplaySettings)."""
    import ctypes
    from ctypes import wintypes as W
    c_long, c_short, c_wchar = ctypes.c_long, ctypes.c_short, ctypes.c_wchar

    class DEVMODEW(ctypes.Structure):
        _fields_ = [("dmDeviceName", c_wchar * 32), ("dmSpecVersion", W.WORD),
                    ("dmDriverVersion", W.WORD), ("dmSize", W.WORD), ("dmDriverExtra", W.WORD),
                    ("dmFields", W.DWORD), ("dmPositionX", c_long), ("dmPositionY", c_long),
                    ("dmDisplayOrientation", W.DWORD), ("dmDisplayFixedOutput", W.DWORD),
                    ("dmColor", c_short), ("dmDuplex", c_short), ("dmYResolution", c_short),
                    ("dmTTOption", c_short), ("dmCollate", c_short), ("dmFormName", c_wchar * 32),
                    ("dmLogPixels", W.WORD), ("dmBitsPerPel", W.DWORD), ("dmPelsWidth", W.DWORD),
                    ("dmPelsHeight", W.DWORD), ("dmDisplayFlags", W.DWORD),
                    ("dmDisplayFrequency", W.DWORD), ("dmICMMethod", W.DWORD),
                    ("dmICMIntent", W.DWORD), ("dmMediaType", W.DWORD), ("dmDitherType", W.DWORD),
                    ("dmReserved1", W.DWORD), ("dmReserved2", W.DWORD),
                    ("dmPanningWidth", W.DWORD), ("dmPanningHeight", W.DWORD)]
    fn = ctypes.windll.user32.EnumDisplaySettingsW
    fn.argtypes = [ctypes.c_wchar_p, W.DWORD, ctypes.POINTER(DEVMODEW)]
    fn.restype = W.BOOL
    out = {}
    for label, idx in (("current", 0xFFFFFFFF), ("registry", 0xFFFFFFFE)):
        dm = DEVMODEW()
        dm.dmSize = ctypes.sizeof(DEVMODEW)
        if fn(None, idx, ctypes.byref(dm)):
            out[label] = "%dx%d, %d Bit, %d Hz" % (dm.dmPelsWidth, dm.dmPelsHeight,
                                                   dm.dmBitsPerPel, dm.dmDisplayFrequency)
    modes, i = set(), 0
    while i < 400:
        dm = DEVMODEW()
        dm.dmSize = ctypes.sizeof(DEVMODEW)
        if not fn(None, i, ctypes.byref(dm)):
            break
        modes.add((dm.dmPelsWidth, dm.dmPelsHeight))
        i += 1
    out["max_mode"] = "%dx%d" % max(modes) if modes else ""
    out["mode_count"] = i
    return out


def sec_explorer():
    s = Sec("Explorer und Taskleiste")
    adv = EXPL + r"\Advanced"
    s.get("Dateiendungen verbergen (HideFileExt)", HKCU_S, adv, "HideFileExt",
          {0: "Endungen sichtbar", 1: "Endungen versteckt"})
    s.get("Versteckte Dateien (Hidden)", HKCU_S, adv, "Hidden",
          {1: "anzeigen", 2: "nicht anzeigen"})
    s.get("Geschützte Systemdateien (ShowSuperHidden)", HKCU_S, adv, "ShowSuperHidden",
          {0: "verbergen", 1: "anzeigen"})
    s.get("Vollständiger Pfad in Titelleiste (FullPath)", HKCU_S,
          r"Software\Microsoft\Windows\CurrentVersion\Explorer\CabinetState", "FullPath")
    s.get("Einfache Dateifreigabe (forceguest)", HKLM_S, r"SYSTEM\CurrentControlSet\Control\Lsa",
          "forceguest", {0: "aus (klassische Freigabe)", 1: "an (Gastzugriff)"})
    s.get("Taskleiste fixiert (TaskbarSizeMove)", HKCU_S, adv, "TaskbarSizeMove",
          {0: "fixiert", 1: "frei"})
    s.get("Taskleisten-Gruppierung (TaskbarGlomLevel)", HKCU_S, adv, "TaskbarGlomLevel",
          {0: "immer", 1: "bei Platzmangel", 2: "nie"})
    s.get("Sprechblasen (EnableBalloonTips)", HKCU_S, adv, "EnableBalloonTips",
          {0: "aus", 1: "an"})
    s.get("Infofelder (ShowInfoTip)", HKCU_S, adv, "ShowInfoTip", {0: "aus", 1: "an"})
    s.get("Inaktive Symbole ausblenden (EnableAutoTray)", HKCU_S, EXPL, "EnableAutoTray",
          {0: "nein", 1: "ja"})
    s.get("Startmenü-Stil: StartMenuInit", HKCU_S, adv, "StartMenuInit")
    s.get("Startmenü klassisch erzwungen (Policy NoSimpleStartMenu)", HKCU_S,
          r"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoSimpleStartMenu",
          {0: "XP-Stil", 1: "klassisch"})
    s.get("Startmenü: Ausführen anzeigen (Start_ShowRun)", HKCU_S, adv, "Start_ShowRun")
    s.get("Startmenü: Programme zählen (Start_TrackProgs)", HKCU_S, adv, "Start_TrackProgs")
    s.get("Tour-Zähler (Tour RunCount)", HKCU_S,
          r"Software\Microsoft\Windows\CurrentVersion\Applets\Tour", "RunCount",
          {0: "Tour ist abgeschaltet"})
    icons = {"{20D04FE0-3AEA-1069-A2D8-08002B30309D}": "Arbeitsplatz",
             "{450D8FBA-AD25-11D0-98A8-0800361B1103}": "Eigene Dateien",
             "{208D2C60-3AEA-1069-A2D7-08002B30309D}": "Netzwerkumgebung",
             "{871C5380-42A0-1069-A2EA-08002B30309D}": "Internet Explorer"}
    for panel in ("NewStartPanel", "ClassicStartMenu"):
        for guid in sorted(icons):
            s.get("Desktopsymbol %s ausblenden (%s)" % (icons[guid], panel), HKCU_S,
                  EXPL + r"\HideDesktopIcons" + "\\" + panel, guid, {0: "sichtbar", 1: "versteckt"})
    return s


def sec_design():
    s = Sec("Desktop und Design")
    s.get("Design aktiv (ThemeActive)", HKCU_S,
          r"Software\Microsoft\Windows\CurrentVersion\ThemeManager", "ThemeActive",
          {"0": "klassisch", "1": "XP-Design"})
    for n in ("DllName", "ColorName", "SizeName"):
        s.get("Design %s" % n, HKCU_S, r"Software\Microsoft\Windows\CurrentVersion\ThemeManager", n)
    s.get("Farbschema klassisch (Appearance)", HKCU_S, r"Control Panel\Appearance", "Current")
    desk = r"Control Panel\Desktop"
    s.get("Hintergrundbild (Wallpaper)", HKCU_S, desk, "Wallpaper")
    s.get("Hintergrund-Stil (WallpaperStyle)", HKCU_S, desk, "WallpaperStyle",
          {"0": "zentriert/Kachel", "2": "strecken", "6": "anpassen"})
    s.get("Bildschirmschoner aktiv (ScreenSaveActive)", HKCU_S, desk, "ScreenSaveActive",
          {"0": "aus", "1": "an"})
    s.get("Bildschirmschoner (SCRNSAVE.EXE)", HKCU_S, desk, "SCRNSAVE.EXE")
    s.get("Bildschirmschoner Wartezeit s (ScreenSaveTimeout)", HKCU_S, desk, "ScreenSaveTimeout")
    s.get("Schoner mit Kennwort (ScreenSaverIsSecure)", HKCU_S, desk, "ScreenSaverIsSecure")
    s.get("Schriftglättung (FontSmoothing)", HKCU_S, desk, "FontSmoothing",
          {"0": "aus", "2": "an"})
    s.get("ClearType (FontSmoothingType)", HKCU_S, desk, "FontSmoothingType",
          {1: "Standard", 2: "ClearType"})
    s.get("Fensterinhalt beim Ziehen (DragFullWindows)", HKCU_S, desk, "DragFullWindows",
          {"0": "aus", "1": "an"})
    s.get("Menüverzögerung ms (MenuShowDelay)", HKCU_S, desk, "MenuShowDelay")
    s.get("UserPreferencesMask", HKCU_S, desk, "UserPreferencesMask")
    s.get("Fenster-Animation (MinAnimate)", HKCU_S, desk + r"\WindowMetrics", "MinAnimate",
          {"0": "aus", "1": "an"})
    try:
        for k, v in sorted(display_modes().items()):
            s.add("Bildschirm %s" % k, v, "EnumDisplaySettings")
    except Exception as e:
        s.add("Bildschirm", "Fehler: %s" % e)
    s.get("Schrift-DPI (LogPixels)", HKLM_S,
          r"SYSTEM\CurrentControlSet\Hardware Profiles\Current\Software\Fonts", "LogPixels")
    s.get("Soundschema (AppEvents Standard)", HKCU_S, r"AppEvents\Schemes", "")
    return s


def sec_region():
    s = Sec("Region, Tastatur, Zeit")
    intl = r"Control Panel\International"
    for n in ("Locale", "sLanguage", "sCountry", "iCountry", "sShortDate", "sLongDate",
              "sTimeFormat", "sDecimal", "sThousand", "sList", "iMeasure", "iPaperSize",
              "s1159", "s2359"):
        s.get("Format %s" % n, HKCU_S, intl, n)
    pre = rvals(HKCU_S, r"Keyboard Layout\Preload")
    for n in sorted(pre, key=lambda x: int(x) if str(x).isdigit() else 99):
        s.add("Tastaturlayout Preload %s" % n, pre[n], r"HKCU\Keyboard Layout\Preload\%s" % n)
    sub = rvals(HKCU_S, r"Keyboard Layout\Substitutes")
    for n in sorted(sub):
        s.add("Layout-Ersatz %s" % n, sub[n], r"HKCU\Keyboard Layout\Substitutes\%s" % n)
    dpre = rvals("HKU", r".DEFAULT\Keyboard Layout\Preload")
    for n in sorted(dpre):
        s.add("Anmeldebildschirm Layout %s" % n, dpre[n], r"HKU\.DEFAULT\Keyboard Layout\Preload\%s" % n)
    s.get("Eingabeumschalter (Hotkey)", HKCU_S, r"Keyboard Layout\Toggle", "Hotkey",
          {"1": "Alt+Shift", "2": "Strg+Shift", "3": "aus"})
    s.get("System-Gebietsschema (Nls Default)", HKLM_S,
          r"SYSTEM\CurrentControlSet\Control\Nls\Language", "Default")
    s.get("Installationssprache (InstallLanguage)", HKLM_S,
          r"SYSTEM\CurrentControlSet\Control\Nls\Language", "InstallLanguage")
    s.get("ANSI-Codepage (ACP)", HKLM_S, r"SYSTEM\CurrentControlSet\Control\Nls\CodePage", "ACP")
    s.get("OEM-Codepage (OEMCP)", HKLM_S, r"SYSTEM\CurrentControlSet\Control\Nls\CodePage", "OEMCP")
    tz = r"SYSTEM\CurrentControlSet\Control\TimeZoneInformation"
    name = s.get("Zeitzone (StandardName)", HKLM_S, tz, "StandardName")
    if name:
        disp = rget(HKLM_S, r"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Time Zones\%s" % name, "Display")
        if disp:
            s.add("Zeitzone Anzeigename", disp, r"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Time Zones\%s\Display" % name)
    bias = s.get("Zeitzone Bias min (Bias)", HKLM_S, tz, "Bias")
    if isinstance(bias, int) and bias > 0x7FFFFFFF:
        s.items[-1]["v"] = bias - 0x100000000
        s.items[-1]["note"] = "vorzeichenbehaftet, -60 = UTC+1"
    s.get("Sommerzeit automatisch aus? (DynamicDaylightTimeDisabled)", HKLM_S, tz,
          "DynamicDaylightTimeDisabled", {0: "Umstellung an", 1: "Umstellung aus"})
    w32 = SVCS + r"\W32Time\Parameters"
    s.get("Zeitsynchronisation Typ (W32Time)", HKLM_S, w32, "Type")
    s.get("Zeitserver (NtpServer)", HKLM_S, w32, "NtpServer")
    return s


def user_flags(name):
    out = sh('net user "%s"' % name)
    keep = ("Account active", "Password required", "Password expires", "Local Group Memberships",
            "Last logon", "User may change password")
    res = []
    for ln in out.splitlines():
        for k in keep:
            if ln.startswith(k):
                res.append("%s=%s" % (k, ln[len(k):].strip()))
    return "; ".join(res)


def sec_users():
    s = Sec("Benutzerkonten und Anmeldung")
    s.add("Server läuft als Benutzer", os.environ.get("USERNAME", ""), "%USERNAME%")
    users = net_names(sh("net user"), 25)
    s.add("Lokale Konten", ", ".join(users), "net user")
    for u in users:
        s.add("Konto %s" % u, user_flags(u), "net user %s" % u)
    for g in ("Administrators", "Users", "Power Users"):
        m = net_names(sh('net localgroup "%s"' % g))
        s.add("Gruppe %s" % g, ", ".join(m), "net localgroup")
    wl = r"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
    s.get("Auto-Login (AutoAdminLogon)", HKLM_S, wl, "AutoAdminLogon", {"0": "aus", "1": "an"})
    s.get("Auto-Login Benutzer (DefaultUserName)", HKLM_S, wl, "DefaultUserName")
    s.get("Auto-Login Domäne/PC (DefaultDomainName)", HKLM_S, wl, "DefaultDomainName")
    pw = rget(HKLM_S, wl, "DefaultPassword")
    s.add("Auto-Login Kennwort im Klartext hinterlegt", "ja" if pw else "nein",
          r"HKLM\%s\DefaultPassword (Wert wird nie ausgegeben)" % wl)
    s.get("Willkommensseite (LogonType)", HKLM_S, wl, "LogonType", {0: "klassische Anmeldung", 1: "Willkommensseite"})
    s.get("Schnelle Benutzerumschaltung (AllowMultipleTSSessions)", HKLM_S, wl,
          "AllowMultipleTSSessions", {0: "aus", 1: "an"})
    s.get("Shell (Winlogon)", HKLM_S, wl, "Shell")
    s.get("Userinit (Winlogon)", HKLM_S, wl, "Userinit")
    s.get("Registrierter Besitzer", HKLM_S, r"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "RegisteredOwner")
    s.get("Registrierte Organisation", HKLM_S, r"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "RegisteredOrganization")
    for sub in rsubs(HKLM_S, r"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList"):
        if sub.startswith("S-1-5-21"):
            s.add("Profilordner", rget(HKLM_S, r"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\%s" % sub,
                                       "ProfileImagePath"), sub)
    return s


def sec_quiet():
    s = Sec("Ruhe (Autoplay, Wiederherstellung, Meldungen, Energie)")
    pol = r"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer"
    s.get("Autoplay Laufwerkstypen deaktiviert HKCU (NoDriveTypeAutoRun)", HKCU_S, pol, "NoDriveTypeAutoRun",
          {255: "alle aus", 145: "0x91 (XP-Standard)", 177: "0xB1 (CD-ROM-Autoplay aus, sonst XP-Standard)"})
    s.get("Autoplay HKLM (NoDriveTypeAutoRun)", HKLM_S, pol, "NoDriveTypeAutoRun",
          {255: "alle aus", 145: "XP-Standard"})
    s.get("CD-Autorun (Cdrom AutoRun)", HKLM_S, SVCS + r"\Cdrom", "AutoRun", {0: "aus", 1: "an"})
    sr = r"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore"
    s.get("Systemwiederherstellung abgeschaltet (DisableSR)", HKLM_S, sr, "DisableSR",
          {0: "an", 1: "aus"})
    s.get("Dienst sr Starttyp", HKLM_S, SVCS + r"\sr", "Start", {0: "Boot", 1: "System", 2: "Auto", 3: "Manuell", 4: "Deaktiviert"})
    s.get("Wiederherstellung Platten-% (DiskPercent)", HKLM_S, sr, "DiskPercent")
    eh = r"SOFTWARE\Microsoft\PCHealth\ErrorReporting"
    s.get("Fehlerberichterstattung (DoReport)", HKLM_S, eh, "DoReport", {0: "aus", 1: "an"})
    s.get("Fehlerbericht-Dialog (ShowUI)", HKLM_S, eh, "ShowUI", {0: "aus", 1: "an"})
    s.get("Dr. Watson/Debugger (AeDebug Debugger)", HKLM_S,
          r"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AeDebug", "Debugger")
    s.get("Dr. Watson Auto (AeDebug Auto)", HKLM_S,
          r"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AeDebug", "Auto")
    cc = r"SYSTEM\CurrentControlSet\Control\CrashControl"
    s.get("Bei Systemfehler neu starten (AutoReboot)", HKLM_S, cc, "AutoReboot", {0: "nein", 1: "ja"})
    s.get("Absturzabbild (CrashDumpEnabled)", HKLM_S, cc, "CrashDumpEnabled",
          {0: "keins", 1: "vollständig", 2: "Kernel", 3: "klein"})
    sc = r"SOFTWARE\Microsoft\Security Center"
    for n in ("AntiVirusDisableNotify", "FirewallDisableNotify", "UpdatesDisableNotify"):
        s.get("Security Center %s" % n, HKLM_S, sc, n, {0: "Meldung an", 1: "Meldung aus"})
    s.get("Dienst wscsvc (Security Center) Starttyp", HKLM_S, SVCS + r"\wscsvc", "Start",
          {2: "Auto", 3: "Manuell", 4: "Deaktiviert"})
    au = r"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update"
    s.get("Automatische Updates (AUOptions)", HKLM_S, au, "AUOptions",
          {1: "aus", 2: "benachrichtigen", 3: "laden", 4: "automatisch"})
    s.get("Automatische Updates Policy (NoAutoUpdate)", HKLM_S,
          r"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", {0: "an", 1: "aus"})
    s.get("Dienst wuauserv Starttyp", HKLM_S, SVCS + r"\wuauserv", "Start",
          {2: "Auto", 3: "Manuell", 4: "Deaktiviert"})
    pw = r"SYSTEM\CurrentControlSet\Control\Power"
    s.get("Ruhezustand aktiviert (HibernateEnabled)", HKLM_S, pw, "HibernateEnabled", {0: "aus", 1: "an"})
    s.get("Energieschema aktuell (PowerCfg)", HKCU_S, r"Control Panel\PowerCfg", "CurrentPowerPolicy")
    for ln in sh("powercfg /list").splitlines():
        if ln.strip() and not ln.strip().startswith("---"):
            s.add("powercfg /list", ln.strip(), "powercfg")
    q = [ln.strip() for ln in sh("powercfg /query").splitlines() if ln.strip()]
    for ln in q[:40]:
        s.add("powercfg /query", ln, "powercfg")
    return s


def sec_network():
    s = Sec("Netzwerk und LAN")
    s.get("Rechnername (ComputerName)", HKLM_S, r"SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName", "ComputerName")
    tcp = SVCS + r"\Tcpip\Parameters"
    s.get("DNS-Domäne (Tcpip Domain)", HKLM_S, tcp, "Domain")
    for ln in sh("net config workstation").splitlines():
        if ln.lower().startswith(("workstation domain", "full computer name", "logon domain")):
            parts = re.split(r"\s{2,}", ln.strip(), 1)
            s.add(parts[0], parts[1] if len(parts) > 1 else "", "net config workstation")
    cards = r"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkCards"
    for sub in rsubs(HKLM_S, cards):
        cv = rvals(HKLM_S, cards + "\\" + sub)
        guid = cv.get("ServiceName")
        if not guid:
            continue
        iv = rvals(HKLM_S, tcp + r"\Interfaces" + "\\" + guid)
        s.add("Netzwerkkarte %s" % sub, cv.get("Description", ""), "NetworkCards\\%s" % sub)
        s.add("  DHCP aktiv (EnableDHCP)", iv.get("EnableDHCP"), "Tcpip\\Interfaces\\%s\\EnableDHCP" % guid)
        for n in ("IPAddress", "DhcpIPAddress", "SubnetMask", "DhcpSubnetMask", "DefaultGateway",
                  "DhcpDefaultGateway", "NameServer", "DhcpNameServer", "Domain", "DhcpServer"):
            if iv.get(n) not in (None, "", "0.0.0.0"):
                s.add("  " + n, iv[n], "Tcpip\\Interfaces\\%s\\%s" % (guid, n))
    for sub in rsubs(HKCU_S, "Network"):
        v = rvals(HKCU_S, "Network\\" + sub)
        s.add("Netzlaufwerk %s:" % sub.upper(), v.get("RemotePath", ""),
              r"HKCU\Network\%s (Benutzer: %s)" % (sub, v.get("UserName", "")))
    for ln in sh("net use").splitlines():
        if ln.strip().startswith(("OK", "Disconnected", "Unavailable")):
            s.add("net use", " ".join(ln.split()), "net use")
    shares = rvals(HKLM_S, SVCS + r"\lanmanserver\Shares")
    for n in sorted(shares):
        s.add("Freigabe %s" % n, shares[n], r"HKLM\%s\lanmanserver\Shares" % SVCS)
    s.get("NetBIOS über TCP/IP Dienst (NetBT) Starttyp", HKLM_S, SVCS + r"\NetBT", "Start")
    fw = {"StandardProfile": "Standardprofil", "DomainProfile": "Domänenprofil"}
    for prof in sorted(fw):
        base = FWP + "\\" + prof
        s.get("Firewall %s aktiv (EnableFirewall)" % fw[prof], HKLM_S, base, "EnableFirewall", {0: "aus", 1: "an"})
        s.get("Firewall %s keine Ausnahmen" % fw[prof], HKLM_S, base, "DoNotAllowExceptions", {0: "Ausnahmen erlaubt", 1: "keine"})
        s.get("Firewall %s Meldungen aus" % fw[prof], HKLM_S, base, "DisableNotifications", {0: "an", 1: "aus"})
        for kind, sub in (("Programm", r"\AuthorizedApplications\List"), ("Port", r"\GloballyOpenPorts\List")):
            v = rvals(HKLM_S, base + sub)
            for n in sorted(v):
                s.add("Firewall %s Ausnahme (%s)" % (fw[prof], kind), v[n], "%s\\%s%s" % (HKLM_S, base, sub))
    s.get("Dienst SharedAccess (Firewall/ICS) Starttyp", HKLM_S, SVCS + r"\SharedAccess", "Start",
          {2: "Auto", 3: "Manuell", 4: "Deaktiviert"})
    ts = r"SYSTEM\CurrentControlSet\Control\Terminal Server"
    s.get("Remote Desktop gesperrt (fDenyTSConnections)", HKLM_S, ts, "fDenyTSConnections", {0: "erlaubt", 1: "gesperrt"})
    s.get("Remoteunterstützung (fAllowToGetHelp)", HKLM_S, ts, "fAllowToGetHelp", {0: "aus", 1: "an"})
    s.get("Dienst TermService Starttyp", HKLM_S, SVCS + r"\TermService", "Start",
          {2: "Auto", 3: "Manuell", 4: "Deaktiviert"})
    return s


def collect_services():
    out = []
    for name in rsubs(HKLM_S, SVCS):
        v = rvals(HKLM_S, SVCS + "\\" + name)
        t = v.get("Type")
        if not isinstance(t, int) or not (t & 0x30):
            continue
        out.append({"name": name, "display": str(v.get("DisplayName", "")),
                    "start": v.get("Start"), "image": str(v.get("ImagePath", "")),
                    "type": t})
    return out


def startup_folder_items():
    res = []
    paths = []
    allp = os.environ.get("ALLUSERSPROFILE")
    usr = os.environ.get("USERPROFILE")
    if allp:
        paths.append(("Alle Benutzer", os.path.join(allp, "Start Menu", "Programs", "Startup")))
    if usr:
        paths.append(("Aktueller Benutzer", os.path.join(usr, "Start Menu", "Programs", "Startup")))
    for label, p in paths:
        try:
            for n in sorted(os.listdir(p)):
                if n.lower() != "desktop.ini":
                    res.append((label, os.path.join(p, n)))
        except OSError:
            pass
    return res


def sec_gaming():
    s = Sec("Spiele-Tuning")
    s.get("Visuelle Effekte (VisualFXSetting)", HKCU_S, EXPL + r"\VisualEffects", "VisualFXSetting",
          {0: "von Windows wählen", 1: "beste Darstellung", 2: "beste Leistung", 3: "benutzerdefiniert"})
    m = rvals(HKCU_S, r"Control Panel\Mouse")
    for n in ("MouseSpeed", "MouseThreshold1", "MouseThreshold2", "MouseSensitivity",
              "SwapMouseButtons", "DoubleClickSpeed"):
        note = ""
        if n == "MouseSpeed":
            note = {"0": "Zeigerbeschleunigung aus", "1": "Beschleunigung niedrig", "2": "Beschleunigung an"}.get(m.get(n), "")
        s.add("Maus %s" % n, m.get(n), r"HKCU\Control Panel\Mouse\%s" % n, note)
    for n in ("KeyboardDelay", "KeyboardSpeed"):
        s.get("Tastatur %s" % n, HKCU_S, r"Control Panel\Keyboard", n)
    mm = r"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management"
    s.get("Prefetch (EnablePrefetcher)", HKLM_S, mm + r"\PrefetchParameters", "EnablePrefetcher",
          {0: "aus", 1: "Anwendungen", 2: "Start", 3: "beides (Standard)"})
    s.get("Auslagerungsdatei (PagingFiles)", HKLM_S, mm, "PagingFiles")
    s.get("Kernel im RAM halten (DisablePagingExecutive)", HKLM_S, mm, "DisablePagingExecutive", {0: "nein", 1: "ja"})
    s.get("Großer Systemcache (LargeSystemCache)", HKLM_S, mm, "LargeSystemCache", {0: "nein", 1: "ja"})
    s.get("Prioritätstrennung (Win32PrioritySeparation)", HKLM_S,
          r"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation",
          {2: "XP-Standard (Vordergrund-Boost 2:1)", 38: "0x26 (Vista-Standard)", 24: "0x18 (Server-Standard)"})
    svcs = collect_services()
    start_names = {0: "Boot", 1: "System", 2: "Auto", 3: "Manuell", 4: "Deaktiviert"}
    auto = sorted(x["name"] for x in svcs if x["start"] == 2)
    dis = sorted(x["name"] for x in svcs if x["start"] == 4)
    man = [x for x in svcs if x["start"] == 3]
    s.add("Dienste Auto (%d)" % len(auto), ", ".join(auto), SVCS + "\\<Name>\\Start=2")
    s.add("Dienste Deaktiviert (%d)" % len(dis), ", ".join(dis), SVCS + "\\<Name>\\Start=4")
    s.add("Dienste Manuell", len(man), SVCS + "\\<Name>\\Start=3")
    for x in svcs:
        img = x["image"].lower()
        if "system32" not in img and "svchost" not in img and "%systemroot%" not in img:
            s.add("Dienst von Dritten: %s" % x["name"], "%s, %s, %s" % (
                start_names.get(x["start"], x["start"]), x["display"], x["image"]), SVCS + "\\" + x["name"])
    for label, hive, path in (("HKLM Run", HKLM_S, r"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
                              ("HKLM RunOnce", HKLM_S, r"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
                              ("HKLM RunServices", HKLM_S, r"SOFTWARE\Microsoft\Windows\CurrentVersion\RunServices"),
                              ("HKCU Run", HKCU_S, r"Software\Microsoft\Windows\CurrentVersion\Run"),
                              ("HKCU RunOnce", HKCU_S, r"Software\Microsoft\Windows\CurrentVersion\RunOnce")):
        v = rvals(hive, path)
        for n in sorted(v):
            s.add("Autostart %s: %s" % (label, n), v[n], "%s\\%s\\%s" % (hive, path, n))
    for label, p in startup_folder_items():
        s.add("Autostart-Ordner (%s)" % label, p, "Startup-Ordner")
    return s


def sec_hardware():
    s = Sec("Grafik und Sound")
    for sub in sorted(rsubs(HKLM_S, CLASS_VIDEO)):
        vid = rvals(HKLM_S, CLASS_VIDEO + "\\" + sub)
        if vid.get("DriverDesc"):
            s.add("Grafik %s" % sub, "%s, %s, Treiber %s (%s)" % (
                vid.get("DriverDesc"), vid.get("ProviderName", ""), vid.get("DriverVersion", ""),
                vid.get("DriverDate", "")), CLASS_VIDEO + "\\" + sub)
    nv = rsubs(HKCU_S, r"Software\NVIDIA Corporation\Global")
    s.add("NVIDIA HKCU Global (Unterschlüssel)", ", ".join(nv), r"HKCU\Software\NVIDIA Corporation\Global")
    for sub in sorted(rsubs(HKLM_S, CLASS_MEDIA)):
        if sub.isdigit():
            v = rvals(HKLM_S, CLASS_MEDIA + "\\" + sub)
            if v.get("DriverDesc") and str(v.get("ProviderName", "")).lower() != "microsoft":
                s.add("Sound %s" % sub, "%s, %s, %s" % (v.get("DriverDesc"), v.get("ProviderName", ""),
                                                          v.get("DriverVersion", "")), CLASS_MEDIA + "\\" + sub)
    return s


SECTIONS = [("explorer", sec_explorer), ("design", sec_design), ("region", sec_region),
            ("users", sec_users), ("quiet", sec_quiet), ("network", sec_network),
            ("gaming", sec_gaming), ("hardware", sec_hardware)]


def op_settings(sock, req):
    only = req.get("only")
    out = []
    for key, fn in SECTIONS:
        if only and key not in only:
            continue
        try:
            sec = fn()
            out.append({"id": key, "title": sec.title, "items": sec.items})
        except Exception as e:
            out.append({"id": key, "title": key, "items": [
                {"k": "Fehler", "v": "%s: %s" % (type(e).__name__, e), "reg": "", "note": ""}]})
    return {"ok": True, "host": os.environ.get("COMPUTERNAME", ""), "user": os.environ.get("USERNAME", ""),
            "sections": out}


XP_KEY_DIGITS = "BCDFGHJKMPQRTVWXY2346789"


def decode_xp_key(raw):
    """Bytes 52..66 der DigitalProductId, Base24 (XP-Algorithmus, ohne N-Sonderfall)."""
    d = list(bytearray(raw[52:67]))
    if len(d) < 15:
        raise ValueError("DigitalProductId zu kurz")
    out = ""
    for _ in range(25):
        cur = 0
        for j in range(14, -1, -1):
            cur = cur * 256 + d[j]
            d[j] = cur // 24
            cur = cur % 24
        out = XP_KEY_DIGITS[cur] + out
    return "-".join(out[i:i + 5] for i in range(0, 25, 5))


def op_productkey(sock, req):
    import winreg
    k = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                       0, winreg.KEY_READ)
    try:
        raw, typ = winreg.QueryValueEx(k, "DigitalProductId")
    finally:
        winreg.CloseKey(k)
    key = decode_xp_key(raw)
    ok = bool(re.match(r"^([%s]{5}-){4}[%s]{5}$" % (XP_KEY_DIGITS, XP_KEY_DIGITS), key))
    log("  productkey gelesen (wird nicht geloggt)")
    return {"ok": True, "key": key, "format_ok": ok}


# ---------- Spiele-Werkzeuge: Verknüpfungen, Desktop-Gruppen, Icon-Positionen, Laufwerke ----------
# lnk launcher erwartet ein eigenes Startskript (Konfiguration "launcher_script", Argumente: Image, Ordner, Exe),
# das ein CD-Image einhängt und die Exe startet. Es gehört nicht zu diesem Repo.

# Vordergrund-Programme, bei denen mount/unmount nichts kaputt macht (sonst spielt vermutlich jemand)
MOUNT_SAFE_FG = ("", "explorer.exe", "python.exe", "cmd.exe", "alcohol.exe", "axcmd.exe", "xpserver.exe")
DESK_SYSTEM = ("my documents", "my computer", "recycle bin", "eigene dateien",
               "arbeitsplatz", "papierkorb", "my network places", "netzwerkumgebung")
_SH = []


def shl():
    """ole32/shell32/kernel32 per ctypes, IShellLinkW + IPersistFile (ohne pywin32)."""
    if _SH:
        return _SH[0]
    import ctypes
    import types
    from ctypes import wintypes
    ns = types.SimpleNamespace()
    ns.ct = ctypes
    ns.wt = wintypes
    ns.ole = ctypes.windll.ole32
    ns.sh = ctypes.windll.shell32
    ns.k = ctypes.windll.kernel32

    class GUID(ctypes.Structure):
        _fields_ = [("d1", ctypes.c_ulong), ("d2", ctypes.c_ushort), ("d3", ctypes.c_ushort),
                    ("d4", ctypes.c_ubyte * 8)]

    def guid(s):
        g = GUID()
        ns.ole.CLSIDFromString(ctypes.c_wchar_p(s), ctypes.byref(g))
        return g
    ns.GUID = GUID
    ns.CLSID_ShellLink = guid("{00021401-0000-0000-C000-000000000046}")
    ns.IID_IShellLinkW = guid("{000214F9-0000-0000-C000-000000000046}")
    ns.IID_IPersistFile = guid("{0000010B-0000-0000-C000-000000000046}")
    ns.ole.CoCreateInstance.argtypes = [ctypes.POINTER(GUID), ctypes.c_void_p, wintypes.DWORD,
                                        ctypes.POINTER(GUID), ctypes.POINTER(ctypes.c_void_p)]
    ns.ole.CoCreateInstance.restype = ctypes.HRESULT
    ns.sh.SHGetFolderPathW.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p,
                                       wintypes.DWORD, ctypes.c_wchar_p]
    ns.sh.SHChangeNotify.argtypes = [ctypes.c_long, ctypes.c_uint, ctypes.c_void_p, ctypes.c_void_p]
    ns.sh.SHChangeNotify.restype = None
    ns.k.GetFileAttributesW.argtypes = [ctypes.c_wchar_p]
    ns.k.GetFileAttributesW.restype = wintypes.DWORD
    ns.k.SetFileAttributesW.argtypes = [ctypes.c_wchar_p, wintypes.DWORD]
    ns.k.CreateFileW.argtypes = [ctypes.c_wchar_p, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                                 wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p]
    ns.k.CreateFileW.restype = ctypes.c_void_p
    ns.k.DeviceIoControl.argtypes = [ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD,
                                     ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD),
                                     ctypes.c_void_p]
    ns.k.CloseHandle.argtypes = [ctypes.c_void_p]
    ns.k.GetVolumeInformationW.argtypes = [ctypes.c_wchar_p, ctypes.c_wchar_p, wintypes.DWORD,
                                           ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
                                           ctypes.c_wchar_p, wintypes.DWORD]
    _SH.append(ns)
    return ns


def com_call(ptr, idx, argtypes, *args):
    """Methode idx der vtable eines COM-Objekts aufrufen (stdcall, HRESULT wirft bei Fehler)."""
    ns = shl()
    ct = ns.ct
    vtbl = ct.cast(ptr, ct.POINTER(ct.POINTER(ct.c_void_p))).contents
    fn = ct.WINFUNCTYPE(ct.HRESULT, ct.c_void_p, *argtypes)(vtbl[idx])
    return fn(ptr, *args)


class ShellLink(object):
    """Eine .lnk-Datei lesen/schreiben. vtable-Reihenfolge laut shobjidl.h (IShellLinkW)."""

    def __init__(self, path):
        ns = shl()
        ct = ns.ct
        self.path = path
        ns.ole.CoInitialize(None)
        p = ct.c_void_p()
        ns.ole.CoCreateInstance(ct.byref(ns.CLSID_ShellLink), None, 1,
                                ct.byref(ns.IID_IShellLinkW), ct.byref(p))
        self.sl = p.value
        pf = ct.c_void_p()
        com_call(self.sl, 0, [ct.POINTER(ns.GUID), ct.POINTER(ct.c_void_p)],
                 ct.byref(ns.IID_IPersistFile), ct.byref(pf))   # QueryInterface
        self.pf = pf.value
        if os.path.exists(path):
            com_call(self.pf, 5, [ct.c_wchar_p, ns.wt.DWORD], path, 0)   # IPersistFile::Load

    def _str(self, idx, n=1024):
        ct = shl().ct
        buf = ct.create_unicode_buffer(n)
        com_call(self.sl, idx, [ct.c_wchar_p, ct.c_int], buf, n)
        return buf.value

    def read(self):
        ns = shl()
        ct = ns.ct
        buf = ct.create_unicode_buffer(1024)
        fd = ct.create_string_buffer(600)          # WIN32_FIND_DATAW (592 Byte)
        com_call(self.sl, 3, [ct.c_wchar_p, ct.c_int, ct.c_void_p, ns.wt.DWORD], buf, 1024, fd, 4)
        ibuf = ct.create_unicode_buffer(1024)
        idx = ct.c_int(0)
        com_call(self.sl, 16, [ct.c_wchar_p, ct.c_int, ct.POINTER(ct.c_int)], ibuf, 1024, ct.byref(idx))
        show = ct.c_int(0)
        com_call(self.sl, 14, [ct.POINTER(ct.c_int)], ct.byref(show))
        return {"path": self.path, "target": buf.value, "args": self._str(10),
                "dir": self._str(8), "icon": "%s,%d" % (ibuf.value, idx.value),
                "show": show.value, "desc": self._str(6)}

    def set(self, target=None, args=None, wdir=None, icon=None, show=None):
        ct = shl().ct
        if target is not None:
            com_call(self.sl, 20, [ct.c_wchar_p], target)
        if args is not None:
            com_call(self.sl, 11, [ct.c_wchar_p], args)
        if wdir is not None:
            com_call(self.sl, 9, [ct.c_wchar_p], wdir)
        if icon is not None:
            f, i = split_icon(icon)
            com_call(self.sl, 17, [ct.c_wchar_p, ct.c_int], f, i)
        if show is not None:
            com_call(self.sl, 15, [ct.c_int], int(show))

    def save(self):
        ct = shl().ct
        com_call(self.pf, 6, [ct.c_wchar_p, ct.c_int], self.path, 1)   # IPersistFile::Save

    def close(self):
        for p in (self.pf, self.sl):
            if p:
                com_call(p, 2, [])   # Release
        self.pf = self.sl = None
        shl().ole.CoUninitialize()


def split_icon(loc):
    p = loc.rfind(",")
    if p > 0 and re.match(r"^-?\d+$", loc[p + 1:].strip()):
        return loc[:p], int(loc[p + 1:])
    return loc, 0


def lnk_read(path):
    l = ShellLink(path)
    try:
        return l.read()
    finally:
        l.close()


def folder_path(csidl):
    ns = shl()
    buf = ns.ct.create_unicode_buffer(260)
    ns.sh.SHGetFolderPathW(None, csidl, None, 0, buf)
    return buf.value


def desk_dirs():
    """(All-Users-Desktop, Desktop des Server-Benutzers)"""
    return folder_path(0x19), folder_path(0x10)   # CSIDL_COMMON_DESKTOPDIRECTORY, CSIDL_DESKTOPDIRECTORY


def all_links():
    """{Name ohne .lnk: Pfad} auf beiden Desktops und in den Gruppenordnern des All-Users-Desktops."""
    au, me = desk_dirs()
    out = {}
    dirs = [au, me] + [os.path.join(au, d) for d in sorted(os.listdir(au))
                       if os.path.isdir(os.path.join(au, d))]
    for d in dirs:
        for f in sorted(os.listdir(d)):
            if f.lower().endswith(".lnk"):
                out[f[:-4]] = os.path.join(d, f)
    return out


def find_link(name):
    """Pfad zu einer Verknüpfung: voller Pfad, exakter Name oder eindeutiger Namensanfang."""
    if name.lower().endswith(".lnk") and ("\\" in name or "/" in name):
        if not os.path.exists(name):
            raise ValueError("keine Datei: %s" % name)
        return name
    links = all_links()
    for k, v in links.items():
        if k.lower() == name.lower():
            return v
    hits = [v for k, v in links.items() if k.lower().startswith(name.lower())]
    if len(hits) != 1:
        raise ValueError("'%s' passt auf %d Verknüpfungen%s" % (
            name, len(hits), (": " + ", ".join(os.path.basename(h)[:-4] for h in hits[:8])) if hits else ""))
    return hits[0]


def icon_of(info):
    """Icon einer Verknüpfung; leer = Ziel-Exe, beim Starter (launch.vbs) die Spiel-Exe."""
    if not info["icon"].startswith(","):
        return info["icon"]
    if launcher_name() and launcher_name() in info["args"].lower():
        q = info["args"].split('"')
        if len(q) >= 8:
            return "%s\\%s,0" % (q[5], q[7])
    return info["target"] + ",0"


def shell_refresh(path=None):
    ns = shl()
    if path:
        ns.sh.SHChangeNotify(0x00001000, 0x0005, ns.ct.c_wchar_p(path), None)   # SHCNE_UPDATEDIR, PATHW
    ns.sh.SHChangeNotify(0x08000000, 0, None, None)   # SHCNE_ASSOCCHANGED: Icons neu laden


def link_dest(name, folder):
    au, _ = desk_dirs()
    d = au if not folder or folder == "-" else os.path.join(au, folder)
    if not os.path.isdir(d):
        os.makedirs(d)
    return os.path.join(d, name + ".lnk")


def backup_link(path):
    bdir = CFG.get("launcher_backup_dir")
    if bdir and os.path.exists(path):
        if not os.path.isdir(bdir):
            os.makedirs(bdir)
        dst = os.path.join(bdir, "%s.%s.lnk" % (os.path.basename(path)[:-4],
                                                           time.strftime("%Y%m%d-%H%M%S")))
        with open(path, "rb") as a, open(dst, "wb") as b:
            b.write(a.read())


def op_lnk(sock, req):
    """show | make | launcher | icon | style"""
    act = req["action"]
    if act == "show":
        out = []
        for n in req["names"]:
            info = lnk_read(find_link(n))
            info["icon_used"] = icon_of(info)
            out.append(info)
        return {"ok": True, "links": out}
    name = req["name"]
    if act in ("make", "launcher"):
        try:
            path = find_link(name)
        except ValueError:
            path = link_dest(name, req.get("folder"))
        if act == "launcher":
            script = cfg_need("launcher_script", "Startskript für lnk launcher")
            backup_link(path)
            wdir, exe = req["dir"].rstrip("\\"), req["exe"]
            target = os.path.join(sysroot(), "system32", "wscript.exe")
            args = '"%s" "%s" "%s" "%s"' % (script, req["image"], wdir, exe)
            icon = req.get("icon") or "%s\\%s,0" % (wdir, exe)
        else:
            target, args, wdir = req["target"], req.get("args") or "", req.get("dir")
            if not wdir:
                wdir = os.path.dirname(target)
            icon = req.get("icon")
        l = ShellLink(path)
        try:
            l.set(target=target, args=args, wdir=wdir, icon=icon, show=int(req.get("show") or 1))
            l.save()
        finally:
            l.close()
    elif act == "icon":
        path = find_link(name)
        l = ShellLink(path)
        try:
            l.set(icon=req["icon"])
            l.save()
        finally:
            l.close()
    elif act == "style":
        path = find_link(name)
        l = ShellLink(path)
        try:
            l.set(show=int(req["show"]))
            l.save()
        finally:
            l.close()
    else:
        return {"ok": False, "error": "unbekannte lnk-Aktion: %s" % act}
    shell_refresh(os.path.dirname(path))
    info = lnk_read(path)
    info["icon_used"] = icon_of(info)
    return {"ok": True, "links": [info]}


def set_folder_icon(folder, loc):
    ns = shl()
    if loc.startswith("@"):
        loc = icon_of(lnk_read(find_link(loc[1:])))
    f, idx = split_icon(loc)
    warn = None if os.path.exists(f) else "Icon-Datei fehlt: %s" % f
    ini = os.path.join(folder, "desktop.ini")
    if os.path.exists(ini):
        ns.k.SetFileAttributesW(ini, 0x80)
        os.remove(ini)
    with open(ini, "w", encoding="mbcs") as t:
        t.write("[.ShellClassInfo]\r\nIconFile=%s\r\nIconIndex=%d\r\nConfirmFileOp=0\r\n" % (f, idx))
    ns.k.SetFileAttributesW(ini, 0x2 | 0x4)          # versteckt + System
    a = ns.k.GetFileAttributesW(folder)
    ns.k.SetFileAttributesW(folder, (a & ~0x2) | 0x1)  # schreibgeschützt = Explorer liest desktop.ini
    shell_refresh(folder)
    return warn


def move_link(path, folder):
    dest = link_dest(os.path.basename(path)[:-4], folder)
    if dest.lower() == path.lower():
        return False
    if os.path.exists(dest):
        os.remove(dest)
    os.rename(path, dest)
    return True


def parse_groups(text):
    pats, icons, grp = {}, {}, None
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("[") and line.endswith("]"):
            grp = line[1:-1]
        elif line.lower().startswith("icon="):
            icons[grp] = line[5:]
        elif grp:
            pats[line.lower()] = grp
    return pats, icons


def op_desk(sock, req):
    """list | move | foldericon | group | ungroup"""
    act = req["action"]
    au, _ = desk_dirs()
    msgs = []

    def where(p):
        d = os.path.dirname(p)
        return "(Desktop)" if d.lower() == au.lower() else os.path.basename(d)
    if act == "list":
        rows = []
        flt = (req.get("filter") or "").lower()
        for k, p in sorted(all_links().items(), key=lambda kv: kv[0].lower()):
            if flt and flt not in k.lower():
                continue
            info = lnk_read(p)
            rows.append({"folder": where(p), "name": k, "icon": icon_of(info),
                         "icon_ok": os.path.exists(split_icon(icon_of(info))[0])})
        return {"ok": True, "links": rows}
    if act == "move":
        move_link(find_link(req["name"]), req["folder"])
        msgs.append("ok")
    elif act == "foldericon":
        w = set_folder_icon(os.path.join(au, req["folder"]), req["icon"])
        msgs.append(w or "ok")
    elif act == "ungroup":
        n = sum(1 for p in all_links().values() if os.path.dirname(p).lower() != au.lower() and move_link(p, "-"))
        msgs.append("%d zurück auf den Desktop" % n)
    elif act == "group":
        pats, icons = parse_groups(req["map"])
        moved = 0
        for k, p in sorted(all_links().items()):
            best, bl = None, 0
            for pat, g in pats.items():
                if len(pat) > bl and k.lower().startswith(pat):
                    best, bl = g, len(pat)
            if best is None:
                msgs.append("ohne Gruppe: %s" % k)
            elif move_link(p, best):
                moved += 1
                msgs.append("%s -> %s" % (k, best))
        for g, loc in icons.items():
            w = set_folder_icon(os.path.join(au, g), loc)
            if w:
                msgs.append("%s: %s" % (g, w))
        msgs.append("%d verschoben" % moved)
    else:
        return {"ok": False, "error": "unbekannte desk-Aktion: %s" % act}
    shell_refresh(au)
    return {"ok": True, "messages": msgs}


def op_deskpos(sock, req):
    """Desktop-Icons verschieben: list | move x y | grid spalte zeile | row zeile namen/--rest"""
    import ctypes
    u32, k32 = ctypes.windll.user32, ctypes.windll.kernel32
    h = u32.FindWindowA(b"Progman", None)
    h = u32.FindWindowExA(h, 0, b"SHELLDLL_DefView", None)
    lv = u32.FindWindowExA(h, 0, b"SysListView32", None)
    if not lv:
        return {"ok": False, "error": "Desktop-ListView nicht gefunden"}
    pid = ctypes.c_ulong()
    u32.GetWindowThreadProcessId(lv, ctypes.byref(pid))
    proc = k32.OpenProcess(0x0008 | 0x0010 | 0x0020 | 0x0400, False, pid.value)
    remote = k32.VirtualAllocEx(proc, None, 4096, 0x1000, 0x04)

    def rread(n, off=0):
        buf = ctypes.create_string_buffer(n)
        k32.ReadProcessMemory(proc, remote + off, buf, n, None)
        return buf.raw

    def items():
        out = []
        for i in range(u32.SendMessageA(lv, 0x1004, 0, 0)):          # LVM_GETITEMCOUNT
            item = (ctypes.c_uint32 * 13)(0, i, 0, 0, 0, remote + 512, 260)
            k32.WriteProcessMemory(proc, remote, ctypes.addressof(item), 52, None)
            n = u32.SendMessageA(lv, 0x102D, i, remote)               # LVM_GETITEMTEXTA
            name = rread(n, 512).decode("mbcs", "replace")
            u32.SendMessageA(lv, 0x1010, i, remote + 2048)            # LVM_GETITEMPOSITION
            x = ctypes.c_int32.from_buffer_copy(rread(4, 2048)).value
            y = ctypes.c_int32.from_buffer_copy(rread(4, 2052)).value
            out.append((i, name, x, y))
        return out

    def find(name, its):
        hits = [t for t in its if t[1].lower() == name.lower()] or \
               [t for t in its if t[1].lower().startswith(name.lower())]
        if len(hits) != 1:
            raise ValueError("'%s' passt auf %d Icons: %s" % (name, len(hits), ", ".join(t[1] for t in hits)))
        return hits[0]

    try:
        s = u32.SendMessageA(lv, 0x1033, 0, 0)                           # LVM_GETITEMSPACING
        cx, cy = s & 0xFFFF, (s >> 16) & 0xFFFF
        r = (ctypes.c_long * 4)()
        u32.SystemParametersInfoA(0x30, 0, ctypes.byref(r), 0)           # SPI_GETWORKAREA
        ww, wh = r[2] - r[0], r[3] - r[1]

        def cell(col, row):
            if col < 0:
                col += ww // cx
            if row < 0:
                row += wh // cy
            return col * cx + (cx - 32) // 2, row * cy + 2

        def setpos(i, x, y):
            u32.SendMessageA(lv, 0x100F, i, (y << 16) | (x & 0xFFFF))   # LVM_SETITEMPOSITION
        warn = None
        if u32.GetWindowLongA(lv, -16) & 0x100:
            warn = "'Automatisch anordnen' ist an, Positionen werden überschrieben"
        its = items()
        act = req.get("action", "list")
        if act == "move":
            setpos(find(req["name"], its)[0], int(req["x"]), int(req["y"]))
        elif act == "grid":
            setpos(find(req["name"], its)[0], *cell(int(req["col"]), int(req["row"])))
        elif act == "row":
            if req.get("rest"):
                sel = sorted((t for t in its if t[1].lower() not in DESK_SYSTEM), key=lambda t: t[1].lower())
            else:
                sel = [find(n, its) for n in req["names"]]
            for col, t in enumerate(sel):
                setpos(t[0], *cell(col, int(req["row"])))
        elif act != "list":
            return {"ok": False, "error": "unbekannte deskpos-Aktion: %s" % act}
        rows = [{"name": n, "x": x, "y": y, "col": int(round(x / float(cx))), "row": int(round(y / float(cy)))}
                for i, n, x, y in items()]
        return {"ok": True, "icons": rows, "grid": [cx, cy], "work": [ww, wh], "warning": warn}
    finally:
        k32.VirtualFreeEx(proc, remote, 0, 0x8000)
        k32.CloseHandle(proc)


def cd_drive(req):
    """Laufwerk der Anfrage, sonst Konfiguration "cd_drive", sonst das erste CD/DVD-Laufwerk."""
    d = req.get("drive") or CFG.get("cd_drive")
    if d:
        return d.rstrip("\\")
    ns = shl()
    ns.k.GetDriveTypeW.argtypes = [ns.ct.c_wchar_p]
    for i in range(26):
        root = "%s:\\" % chr(65 + i)
        if ns.k.GetDriveTypeW(root) == 5:
            return root[:2]
    raise ValueError("kein CD/DVD-Laufwerk gefunden (Konfiguration cd_drive oder --drive)")


def vol_label(drive):
    ns = shl()
    buf = ns.ct.create_unicode_buffer(64)
    fs = ns.ct.create_unicode_buffer(64)
    ok = ns.k.GetVolumeInformationW(drive.rstrip("\\") + "\\", buf, 64, None, None, None, fs, 64)
    return buf.value if ok else None


def fg_process():
    w = win()
    fg = w.u.GetForegroundWindow() or 0
    if not fg:
        return "", 0, ""
    pid = w.wt.DWORD(0)
    w.u.GetWindowThreadProcessId(fg, w.ct.byref(pid))
    return w_exe(w, pid.value), pid.value, w_text(w, fg)


def mount_guard(req):
    if req.get("force"):
        return None
    exe, pid, title = fg_process()
    if exe.lower() not in MOUNT_SAFE_FG:
        return ("Im Vordergrund läuft %s (pid %d, \"%s\"): spielt gerade jemand? "
                "Nichts gemountet. --force erzwingt es." % (exe, pid, title))
    return None


def ax(args):
    axcmd = cfg_need("alcohol_axcmd", "Pfad zu AxCmd.exe (Alcohol 120%)")
    p = subprocess.Popen('"%s" %s' % (axcmd, args), stdin=subprocess.DEVNULL,
                         stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    out, _ = p.communicate(timeout=60)
    return out.decode(oem_encoding(), "replace").strip()


def op_mount(sock, req):
    drive = (req.get("drive") or cfg_need("image_drive", "virtuelles Alcohol-Laufwerk, z. B. \"G:\"")).rstrip("\\")
    cfg_need("alcohol_axcmd", "Pfad zu AxCmd.exe (Alcohol 120%)")
    stop = mount_guard(req)
    if stop:
        return {"ok": False, "error": stop}
    before = vol_label(drive)
    ax("%s /U" % drive)
    label = None
    if req.get("image"):
        ax('%s /M:"%s"' % (drive, req["image"]))
        for _ in range(40):
            label = vol_label(drive)
            if label is not None:
                break
            time.sleep(0.5)
        if label is None:
            return {"ok": False, "error": "Image hängt nicht auf %s: %s" % (drive, req["image"])}
    return {"ok": True, "drive": drive, "before": before, "label": label}


def op_eject(sock, req):
    ns = shl()
    drive = cd_drive(req)
    h = ns.k.CreateFileW("\\\\.\\" + drive, 0x80000000, 0x3, None, 3, 0, None)
    if not h or h == ns.ct.c_void_p(-1).value:
        return {"ok": False, "error": "Laufwerk %s nicht zu öffnen" % drive}
    try:
        n = ns.wt.DWORD(0)
        ok = ns.k.DeviceIoControl(h, 0x2D4808, None, 0, None, 0, ns.ct.byref(n), None)  # IOCTL_STORAGE_EJECT_MEDIA
    finally:
        ns.k.CloseHandle(h)
    return {"ok": bool(ok), "error": None if ok else "Auswerfen fehlgeschlagen (Fehler %d)" % ns.ct.GetLastError()}


def op_ripiso(sock, req):
    """Datenspur einer ungeschützten CD 1:1 als ISO (Rohgerät \\\\.\\E:). Länge aus dem ISO9660-PVD.
    Unlesbar darf nur der Auslauf am Disc-Ende sein (TAO-Run-out, max. 150 Sektoren), dort Nullen."""
    import hashlib as _h
    drive, target = cd_drive(req).rstrip(":"), req["target"]
    sec, tail = 2048, 150
    parent = os.path.dirname(target)
    if parent and not os.path.isdir(parent):
        os.makedirs(parent)
    start = time.time()
    with open("\\\\.\\%s:" % drive, "rb", buffering=0) as src:
        src.seek(16 * sec)
        pvd = src.read(sec)
        if pvd[1:6] != b"CD001":
            return {"ok": False, "error": "kein ISO9660-Datenträger in %s:" % drive}
        label = pvd[40:72].decode("ascii", "replace").strip()
        sectors = struct.unpack_from("<I", pvd, 80)[0]
        md5, done, padded = _h.md5(), 0, []

        def read(first, count):
            src.seek(first * sec)
            data = src.read(count * sec)
            if len(data) != count * sec:
                raise IOError("kurz gelesen")
            return data
        with open(target, "wb") as dst:
            while done < sectors:
                n = min(32, sectors - done)
                try:
                    data = read(done, n)
                except (IOError, OSError):
                    data = b""
                    for s in range(done, done + n):
                        try:
                            data += read(s, 1)
                        except (IOError, OSError):
                            if sectors - s > tail:
                                return {"ok": False, "error": "Lesefehler bei Sektor %d (nicht im Auslauf, "
                                        "Disc prüfen oder Kopierschutz: dann Alcohol MDF/MDS)" % s}
                            padded.append(s)
                            data += b"\0" * sec
                dst.write(data)
                md5.update(data)
                done += n
    return {"ok": True, "label": label, "sectors": done, "md5": md5.hexdigest(),
            "seconds": int(time.time() - start), "padded": [padded[0], padded[-1], len(padded)] if padded else None,
            "target": target}


def op_pesec(sock, req):
    """PE-Sektionsnamen (Kopierschutz-Hinweis: stxt774/stxt371 = SafeDisc 2+, .securom = SecuROM)."""
    out = {}
    for p in req["paths"]:
        try:
            with open(p, "rb") as f:
                d = f.read(4096)
            o = struct.unpack_from("<I", d, 0x3C)[0]
            n = struct.unpack_from("<H", d, o + 6)[0]
            so = o + 24 + struct.unpack_from("<H", d, o + 20)[0]
            out[p] = [d[so + 40 * i:so + 40 * i + 8].rstrip(b"\0").decode("latin1") for i in range(n)]
        except Exception as e:
            out[p] = "Fehler: %s" % e
    return {"ok": True, "sections": out}


def op_vol(sock, req):
    drives = req.get("drives")
    if not drives:
        drives = [d for d in (CFG.get("cd_drive"), CFG.get("image_drive")) if d]
    if not drives:
        drives = [cd_drive({})]
    return {"ok": True, "drives": dict((d, vol_label(d)) for d in drives)}


# ---------- Treiber (SetupAPI per ctypes) ----------

def present_devices(filt=None):
    """Angeschlossene Geräte: Hardware-IDs, Beschreibung, Dienst, Treiber-Schlüssel, Status/Problemcode."""
    import ctypes as ct
    from ctypes import wintypes as wt

    class DEVINFO(ct.Structure):
        _fields_ = [("cbSize", wt.DWORD), ("ClassGuid", ct.c_byte * 16),
                    ("DevInst", wt.DWORD), ("Reserved", ct.c_void_p)]
    sa = ct.windll.setupapi
    sa.SetupDiGetClassDevsW.restype = ct.c_void_p
    sa.SetupDiGetClassDevsW.argtypes = [ct.c_void_p, wt.LPCWSTR, wt.HWND, wt.DWORD]
    sa.SetupDiEnumDeviceInfo.argtypes = [ct.c_void_p, wt.DWORD, ct.POINTER(DEVINFO)]
    sa.SetupDiGetDeviceRegistryPropertyW.argtypes = [ct.c_void_p, ct.POINTER(DEVINFO), wt.DWORD,
                                                     ct.POINTER(wt.DWORD), ct.c_void_p, wt.DWORD,
                                                     ct.POINTER(wt.DWORD)]
    sa.SetupDiDestroyDeviceInfoList.argtypes = [ct.c_void_p]
    h = sa.SetupDiGetClassDevsW(None, None, None, 0x2 | 0x4)  # DIGCF_PRESENT | DIGCF_ALLCLASSES
    if not h or h == ct.c_void_p(-1).value:
        raise OSError("SetupDiGetClassDevs fehlgeschlagen")
    out = []
    try:
        i = 0
        while True:
            d = DEVINFO()
            d.cbSize = ct.sizeof(DEVINFO)
            if not sa.SetupDiEnumDeviceInfo(h, i, ct.byref(d)):
                break
            i += 1

            def prop(code):
                buf = ct.create_unicode_buffer(2048)
                if not sa.SetupDiGetDeviceRegistryPropertyW(h, ct.byref(d), code, None, buf,
                                                            ct.sizeof(buf), None):
                    return []
                return [s for s in ct.wstring_at(ct.addressof(buf), 2048).split("\0") if s]
            ids = prop(1) + prop(2)  # SPDRP_HARDWAREID, SPDRP_COMPATIBLEIDS
            desc = (prop(12) or prop(0) or [""])[0]  # FRIENDLYNAME, DEVICEDESC
            if filt and not any(filt.lower() in s.lower() for s in ids + [desc]):
                continue
            st, prob = wt.ULONG(0), wt.ULONG(0)
            cr = ct.windll.cfgmgr32.CM_Get_DevNode_Status(ct.byref(st), ct.byref(prob), d.DevInst, 0)
            out.append({"ids": ids, "desc": desc, "service": (prop(4) or [""])[0],
                        "driver": (prop(9) or [""])[0], "problem": prob.value if cr == 0 else None,
                        "started": bool(st.value & 0x8) if cr == 0 else None})  # DN_STARTED
    finally:
        sa.SetupDiDestroyDeviceInfoList(h)
    return out


def op_driver(sock, req):
    """list [filter] | install INF: INF für alle angeschlossenen Geräte erzwingen, deren Hardware-ID
    in der INF steht (UpdateDriverForPlugAndPlayDevices), sonst nur in den Treiberspeicher legen
    (SetupCopyOEMInf, greift beim Einstecken). Unsignierte Treiber: XP zeigt dabei den
    Logo-Dialog, der blockiert, bis "Continue Anyway" gedrückt ist (press --label)."""
    import ctypes as ct
    from ctypes import wintypes as wt
    if req.get("action") == "list":
        return {"ok": True, "devices": present_devices(req.get("filter"))}
    inf = req["inf"]
    if not os.path.isfile(inf):
        return {"ok": False, "error": "INF fehlt: %s" % inf}
    with open(inf, "rb") as f:
        text = f.read().decode("latin1").upper()
    hits = []
    for dev in present_devices():
        hid = next((s for s in dev["ids"] if s.upper() in text and "\\" in s), None)
        if hid and hid not in hits:
            hits.append(hid)
    if req.get("hwid"):
        hits = [req["hwid"]]
    results = []
    if hits:
        nd = ct.windll.newdev
        nd.UpdateDriverForPlugAndPlayDevicesW.argtypes = [wt.HWND, wt.LPCWSTR, wt.LPCWSTR, wt.DWORD,
                                                          ct.POINTER(wt.BOOL)]
        for hid in hits:
            reboot = wt.BOOL(0)
            ok = nd.UpdateDriverForPlugAndPlayDevicesW(None, hid, inf, 0x1, ct.byref(reboot))  # INSTALLFLAG_FORCE
            results.append({"hwid": hid, "ok": bool(ok), "reboot": bool(reboot.value),
                            "error": None if ok else ct.GetLastError() & 0xFFFFFFFF})
    if results:
        good = all(r["ok"] for r in results)
        return {"ok": good, "devices": results,
                "error": None if good else "Installation fehlgeschlagen: " + ", ".join(
                    "%s Fehler %#x" % (r["hwid"], r["error"]) for r in results if not r["ok"])}
    sa = ct.windll.setupapi
    sa.SetupCopyOEMInfW.argtypes = [wt.LPCWSTR, wt.LPCWSTR, wt.DWORD, wt.DWORD, wt.LPWSTR, wt.DWORD,
                                    ct.POINTER(wt.DWORD), ct.c_void_p]
    buf = ct.create_unicode_buffer(260)
    ok = sa.SetupCopyOEMInfW(inf, None, 1, 0, buf, 260, None, None)  # SPOST_PATH
    oem = {"ok": bool(ok), "path": buf.value,
           "error": None if ok else ct.GetLastError() & 0xFFFFFFFF}
    return {"ok": oem["ok"], "devices": [], "oeminf": oem,
            "error": None if ok else "SetupCopyOEMInf Fehler %s" % oem["error"]}


# ---------- Spiele-Versionen ----------

def exe_version(path):
    """VERSIONINFO einer Exe/DLL: feste Datei-/Produktversion und die Strings, dazu Größe, Datum, md5."""
    import ctypes as ct
    from ctypes import wintypes as wt
    out = {"path": path}
    try:
        st = os.stat(path)
    except OSError:
        out["missing"] = True
        return out
    out["size"] = st.st_size
    out["date"] = time.strftime("%Y-%m-%d", time.localtime(st.st_mtime))
    if st.st_size <= 64 * 1024 * 1024:
        h = hashlib.md5()
        with open(path, "rb") as f:
            for blk in iter(lambda: f.read(1024 * 1024), b""):
                h.update(blk)
        out["md5"] = h.hexdigest()
    v = ct.windll.version
    v.GetFileVersionInfoSizeW.argtypes = [wt.LPCWSTR, ct.POINTER(wt.DWORD)]
    v.GetFileVersionInfoW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, ct.c_void_p]
    v.VerQueryValueW.argtypes = [ct.c_void_p, wt.LPCWSTR, ct.POINTER(ct.c_void_p), ct.POINTER(wt.UINT)]
    n = v.GetFileVersionInfoSizeW(path, None)
    if not n:
        return out
    buf = ct.create_string_buffer(n)
    if not v.GetFileVersionInfoW(path, 0, n, buf):
        return out
    ptr, ln = ct.c_void_p(), wt.UINT(0)
    if v.VerQueryValueW(buf, "\\", ct.byref(ptr), ct.byref(ln)) and ln.value >= 52:
        d = (ct.c_uint32 * 13).from_address(ptr.value)
        if d[0] == 0xFEEF04BD:
            out["file_version"] = "%d.%d.%d.%d" % (d[2] >> 16, d[2] & 0xFFFF, d[3] >> 16, d[3] & 0xFFFF)
            out["product_version"] = "%d.%d.%d.%d" % (d[4] >> 16, d[4] & 0xFFFF, d[5] >> 16, d[5] & 0xFFFF)
    langs = []
    if v.VerQueryValueW(buf, "\\VarFileInfo\\Translation", ct.byref(ptr), ct.byref(ln)) and ln.value >= 4:
        arr = (ct.c_uint16 * (ln.value // 2)).from_address(ptr.value)
        langs = ["%04x%04x" % (arr[i], arr[i + 1]) for i in range(0, len(arr) - 1, 2)]
    strings = {}
    for lang in langs + ["040904b0", "040904e4", "040704b0", "040704e4"]:
        for key in ("ProductName", "ProductVersion", "FileVersion", "FileDescription", "CompanyName",
                    "LegalCopyright", "OriginalFilename", "SpecialBuild", "PrivateBuild", "Comments"):
            if key in strings:
                continue
            if v.VerQueryValueW(buf, "\\StringFileInfo\\%s\\%s" % (lang, key), ct.byref(ptr),
                                ct.byref(ln)) and ln.value:
                val = ct.wstring_at(ptr.value, ln.value).rstrip("\0").strip()
                if val:
                    strings[key] = val
    out["strings"] = strings
    out["langs"] = langs
    return out


GAME_MARKERS = re.compile(r"(?i)^(goggame-\d+\.info|gameinfo|version(\.txt|\.ini|\.dat)?|.*\.ver|"
                          r"readme.*|liesmich.*|patch.*\.txt|changes.*|changelog.*|.*update.*\.txt|whatsnew.*)$")


def game_root(exe):
    gdir = (CFG.get("games_dir") or "").rstrip("\\")
    norm = os.path.normpath(exe)
    if gdir and norm.lower().startswith(gdir.lower() + "\\"):
        first = norm[len(gdir) + 1:].split("\\")[0]
        if first and first.lower() != os.path.basename(norm).lower():
            return gdir + "\\" + first
    return os.path.dirname(exe)


def game_markers(root):
    """Versionshinweise im Spielordner (Tiefe 2): GOG-Infodateien ausgelesen, sonst nur Dateinamen."""
    found = {}
    for base, dirs, files in os.walk(root):
        if base[len(root):].count("\\") >= 2:
            dirs[:] = []
        for f in files:
            if not GAME_MARKERS.match(f):
                continue
            p = os.path.join(base, f)
            rel = p[len(root) + 1:]
            low = f.lower()
            if low.startswith("goggame-") or low == "gameinfo" or low.startswith("version"):
                try:
                    with open(p, "rb") as fh:
                        txt = fh.read(4096).decode("latin1")
                    if low.startswith("goggame-"):
                        try:
                            j = json.loads(txt)
                            txt = dict((k, j.get(k)) for k in ("name", "gameId", "buildId", "version",
                                                               "language", "languages") if j.get(k))
                        except ValueError:
                            pass
                    found[rel] = txt if isinstance(txt, dict) else txt.strip()[:400]
                except OSError:
                    found[rel] = None
            else:
                found[rel] = None
        if len(found) > 40:
            break
    return found


def link_game(name, path):
    info = lnk_read(path)
    target, args = info["target"], info["args"] or ""
    img = None
    if target.lower().endswith("wscript.exe") and launcher_name() and launcher_name() in args.lower():
        q = re.findall(r'"([^"]*)"', args)
        if len(q) >= 4:
            img, exe = q[1], os.path.join(q[2], q[3])
        else:
            exe = target
    else:
        exe = target
    return {"name": name, "folder": os.path.basename(os.path.dirname(path)), "exe": exe,
            "args": args if not img else None, "image": img, "root": game_root(exe)}


def op_games(sock, req):
    """Alle Spiele-Verknüpfungen (oder Filter): Exe-Version, GOG-/Versionsdateien, Uninstall-Eintrag."""
    filt = (req.get("filter") or "").lower()
    progs = collect_programs()
    out = []
    for name, path in sorted(all_links().items(), key=lambda x: x[0].lower()):
        if filt and filt not in name.lower():
            continue
        try:
            g = link_game(name, path)
        except Exception as e:
            out.append({"name": name, "error": "%s: %s" % (type(e).__name__, e)})
            continue
        if not g["exe"].lower().endswith(".exe") or "\\windows\\" in g["exe"].lower():
            g["skip"] = True
            out.append(g)
            continue
        g["exe_info"] = exe_version(g["exe"])
        root = g["root"]
        g["markers"] = game_markers(root) if os.path.isdir(root) and len(root.rstrip("\\")) > 3 else {}
        g["uninstall"] = [{"name": p["name"], "version": p["version"], "date": p["date"]}
                          for p in progs if p.get("location") and
                          (os.path.normcase(p["location"]).rstrip("\\") + "\\").startswith(os.path.normcase(root) + "\\")]
        out.append(g)
    return {"ok": True, "games": out}


# ---------- Dateien, Prozesse, Discs (statt run 'dir/findstr/type/tasklist/taskkill/copy/del') ----------

import fnmatch
import shutil

NO_KILL = ("explorer.exe", "csrss.exe", "winlogon.exe", "lsass.exe", "services.exe", "smss.exe",
           "svchost.exe", "system", "python.exe", "pythonw.exe")


def _match(name, patterns):
    return not patterns or any(fnmatch.fnmatch(name.lower(), p.lower()) for p in patterns)


def _stamp(t):
    return time.strftime("%Y-%m-%d %H:%M", time.localtime(t))


def op_ls(sock, req):
    """Dateien/Ordner mit Größe und Datum. recurse = Tiefe (0 = nur dieser Ordner, -1 = alles)."""
    root, pats, depth = req["path"], req.get("patterns") or [], req.get("recurse", 0)
    limit = req.get("limit") or 2000
    if os.path.isfile(root):
        st = os.stat(root)
        return {"ok": True, "entries": [{"path": root, "size": st.st_size, "mtime": _stamp(st.st_mtime),
                                         "dir": False}], "total": st.st_size, "truncated": False}
    if not os.path.isdir(root):
        return {"ok": False, "error": "gibt es nicht: %s" % root}
    out, total, base_depth = [], 0, root.rstrip("\\").count("\\")
    for base, dirs, files in os.walk(root):
        level = base.rstrip("\\").count("\\") - base_depth
        if depth >= 0 and level >= depth:
            show_dirs, dirs[:] = list(dirs), []
        else:
            show_dirs = list(dirs)
        dirs.sort(key=str.lower)
        for d in sorted(show_dirs, key=str.lower):
            if not req.get("files_only") and _match(d, pats):
                out.append({"path": os.path.join(base, d), "dir": True})
        for f in sorted(files, key=str.lower):
            if not _match(f, pats):
                continue
            pth = os.path.join(base, f)
            try:
                st = os.stat(pth)
                out.append({"path": pth, "size": st.st_size, "mtime": _stamp(st.st_mtime), "dir": False})
                total += st.st_size
            except OSError as e:
                out.append({"path": pth, "error": str(e), "dir": False})
        if len(out) >= limit:
            break
    return {"ok": True, "entries": out[:limit], "total": total, "truncated": len(out) >= limit}


def _decode(data):
    for enc in ("utf-8", "cp1252"):
        try:
            return data.decode(enc)
        except UnicodeDecodeError:
            pass
    return data.decode("latin1")


def op_cat(sock, req):
    """Textdatei lesen: grep (Teilstring oder /regex/), head/tail, max. 4 MB vom Ende."""
    path = req["path"]
    size = os.path.getsize(path)
    cap = 4 * 1024 * 1024
    with open(path, "rb") as f:
        if size > cap:
            f.seek(size - cap)
        text = _decode(f.read())
    lines = text.splitlines()
    g = req.get("grep")
    if g:
        if len(g) > 2 and g.startswith("/") and g.endswith("/"):
            rx = re.compile(g[1:-1], re.I)
            lines = ["%d: %s" % (i + 1, l) for i, l in enumerate(lines) if rx.search(l)]
        else:
            lines = ["%d: %s" % (i + 1, l) for i, l in enumerate(lines) if g.lower() in l.lower()]
    if req.get("tail"):
        lines = lines[-req["tail"]:]
    if req.get("head"):
        lines = lines[:req["head"]]
    return {"ok": True, "size": size, "cut": size > cap, "lines": lines[:20000]}


def _processes():
    p = subprocess.Popen(["tasklist", "/fo", "csv", "/nh"], stdin=subprocess.DEVNULL,
                         stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    out, _ = p.communicate(timeout=30)
    procs = []
    for line in out.decode(oem_encoding(), "replace").splitlines():
        cols = [c.strip('"') for c in line.split('","')]
        if len(cols) >= 5:
            procs.append({"name": cols[0].strip('"'), "pid": int(cols[1]), "mem_kb":
                          int(re.sub(r"[^0-9]", "", cols[4]) or 0)})
    return procs


def op_ps(sock, req):
    """Prozesse (tasklist), Filter = Teil des Namens, cmdline = Befehlszeilen per wmic."""
    filt = (req.get("filter") or "").lower()
    procs = [p for p in _processes() if filt in p["name"].lower()]
    if req.get("cmdline") and procs:
        q = " or ".join("ProcessId=%d" % p["pid"] for p in procs[:40])
        pr = subprocess.Popen('wmic process where "%s" get ProcessId,CommandLine /format:list' % q,
                              stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        out, _ = pr.communicate(timeout=60)
        cur = {}
        for line in out.decode(oem_encoding(), "replace").replace("\r", "").split("\n"):
            if line.startswith("CommandLine="):
                cur["cmd"] = line[12:]
            elif line.startswith("ProcessId="):
                pid = int(line[10:] or 0)
                for p in procs:
                    if p["pid"] == pid:
                        p["cmdline"] = cur.get("cmd", "")
                cur = {}
    return {"ok": True, "procs": procs}


def op_kill(sock, req):
    """Prozesse beenden: pids oder Name (exakt, z.B. xcopy.exe). Systemprozesse nur mit force."""
    targets = []
    procs = _processes()
    for t in req["targets"]:
        if str(t).isdigit():
            targets += [p for p in procs if p["pid"] == int(t)]
        else:
            targets += [p for p in procs if p["name"].lower() == str(t).lower()]
    if not targets:
        return {"ok": False, "error": "kein passender Prozess"}
    blocked = [p for p in targets if p["name"].lower() in NO_KILL and not req.get("force")]
    if blocked:
        return {"ok": False, "error": "geschützt (force nötig): %s" % ", ".join(
            "%s %d" % (p["name"], p["pid"]) for p in blocked)}
    res = []
    for p in targets:
        args = ["taskkill", "/F", "/PID", str(p["pid"])] + (["/T"] if req.get("tree") else [])
        rc = subprocess.call(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        res.append({"name": p["name"], "pid": p["pid"], "ok": rc == 0})
    time.sleep(1)
    alive = set(p["pid"] for p in _processes())
    for r in res:
        r["still_running"] = r["pid"] in alive
    return {"ok": all(r["ok"] for r in res), "killed": res,
            "error": None if all(r["ok"] for r in res) else "nicht alle beendet (hängt evtl. im Kernel-I/O)"}


def _md5(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for blk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(blk)
    return h.hexdigest()


def op_hash(sock, req):
    algo = req.get("algo") or "md5"
    out = {}
    for p in req["paths"]:
        try:
            h = hashlib.new(algo)
            with open(p, "rb") as f:
                for blk in iter(lambda: f.read(1024 * 1024), b""):
                    h.update(blk)
            out[p] = h.hexdigest()
        except Exception as e:
            out[p] = "Fehler: %s" % e
    return {"ok": True, "algo": algo, "hashes": out}


def op_copy(sock, req):
    """Datei oder Ordner kopieren, je Datei weiter bei Lesefehler (meldet die unlesbaren), verify = md5-Vergleich.
    Ziel mit \\ am Ende oder vorhandener Ordner = hinein kopieren."""
    src, dst = req["src"], req["dst"]
    if os.path.isdir(dst) or dst.endswith("\\"):
        dst = os.path.join(dst, os.path.basename(src.rstrip("\\")) or "disc")
    pairs = []
    if os.path.isdir(src):
        for base, dirs, files in os.walk(src):
            for f in files:
                s = os.path.join(base, f)
                pairs.append((s, os.path.join(dst, os.path.relpath(s, src))))
    else:
        pairs.append((src, dst))
    done, failed, total, start = [], [], 0, time.time()
    for s, d in pairs:
        try:
            parent = os.path.dirname(d)
            if parent and not os.path.isdir(parent):
                os.makedirs(parent)
            shutil.copy2(s, d)
            size = os.path.getsize(d)
            total += size
            item = {"src": s, "size": size}
            if req.get("verify"):
                a, b = _md5(s), _md5(d)
                item["md5"] = a
                if a != b:
                    failed.append({"src": s, "error": "md5 weicht ab (%s / %s)" % (a, b)})
                    continue
            done.append(item)
        except (IOError, OSError, shutil.Error) as e:
            failed.append({"src": s, "error": str(e)})
    return {"ok": not failed, "copied": done, "failed": failed, "bytes": total,
            "seconds": int(time.time() - start), "dst": dst,
            "error": None if not failed else "%d Datei(en) nicht kopiert" % len(failed)}


def op_rm(sock, req):
    """Löschen. Ohne force nur in den Ordnern aus der Konfiguration "rm_roots" und auf den Desktops."""
    removed, errors = [], []
    au, me = desk_dirs()
    roots = tuple(p.rstrip("\\").lower() + "\\" for p in CFG.get("rm_roots") or [])
    safe = roots + (au.lower() + "\\", me.lower() + "\\")
    for p in req["paths"]:
        full = os.path.abspath(p)
        if not req.get("force") and not full.lower().startswith(safe):
            errors.append("%s: außerhalb der freigegebenen Ordner (%s)" % (p, "force nötig" if roots else
                          'nicht konfiguriert: "rm_roots" in %s, sonst force' % CONFIG_NAME))
            continue
        try:
            if os.path.isdir(full):
                if not req.get("recurse"):
                    errors.append("%s: Ordner, recurse nötig" % p)
                    continue
                shutil.rmtree(full)
            else:
                os.remove(full)
            removed.append(full)
        except OSError as e:
            errors.append("%s: %s" % (p, e))
    return {"ok": not errors, "removed": removed, "error": "; ".join(errors) if errors else None}


PROTECT_FILES = [
    ("SafeDisc 1", ["00000001.tmp", "*.icd", "clcd16.dll", "clcd32.dll", "clokspl.exe", "dplayerx.dll"]),
    ("SafeDisc 2+", ["00000002.tmp", "secdrv.sys", "drvmgt.dll"]),
    ("SecuROM", ["cms16.dll", "cms_95.dll", "cms_nt.dll", "cms32_95.dll", "cms32_nt.dll", "*.securom"]),
    ("LaserLock", ["laserlok.*", "nomouse.*", "*.laserlock"]),
    ("StarForce", ["protect.dll", "protect.exe", "protect.x64", "protect.x86"]),
    ("TAGES", ["tages*.*", "*.tagesprotection"]),
    ("CD-Cops", ["cdcops.dll", "*.gz_", "*.w_x"]),
]
PROTECT_SECTIONS = {"stxt774": "SafeDisc 2+", "stxt371": "SafeDisc 2+", ".securom": "SecuROM",
                    ".laser": "LaserLock", ".icd": "SafeDisc", "bitarts": "Crunch/Bitarts"}


def pe_sections(path):
    with open(path, "rb") as f:
        d = f.read(4096)
    o = struct.unpack_from("<I", d, 0x3C)[0]
    n = struct.unpack_from("<H", d, o + 6)[0]
    so = o + 24 + struct.unpack_from("<H", d, o + 20)[0]
    return [d[so + 40 * i:so + 40 * i + 8].rstrip(b"\0").decode("latin1") for i in range(n)]


def op_disc(sock, req):
    """CD/DVD prüfen: Label, Wurzel, Kopierschutz-Dateien (bis Tiefe 3) und PE-Sektionen der Exes in Wurzel + 1 Ebene."""
    drive = cd_drive(req)
    label = vol_label(drive)
    if label is None:
        return {"ok": False, "error": "kein Medium in %s" % drive}
    root = drive + "\\"
    top = sorted(os.listdir(root), key=str.lower)
    hits, exes, rootlen = {}, [], len(root)
    for base, dirs, files in os.walk(root):
        level = base[rootlen:].count("\\") + (1 if base[rootlen:] else 0)
        if level >= 3:
            dirs[:] = []
        for f in files:
            for kind, pats in PROTECT_FILES:
                if _match(f, pats):
                    hits.setdefault(kind, []).append(os.path.join(base, f)[rootlen:])
            if f.lower().endswith(".exe") and level <= 1:
                exes.append(os.path.join(base, f))
    secs = {}
    for e in exes[:30]:
        try:
            sl = pe_sections(e)
            secs[e[rootlen:]] = sl
            for s in sl:
                k = PROTECT_SECTIONS.get(s.lower())
                if k:
                    hits.setdefault(k, []).append("%s [%s]" % (e[rootlen:], s))
        except Exception as ex:
            secs[e[rootlen:]] = "Fehler: %s" % ex
    return {"ok": True, "drive": drive, "label": label, "root": top, "protection": hits, "sections": secs}


def op_setupapi(sock, req):
    """Letzte Abschnitte aus setupapi.log im Windows-Ordner (Treiberinstallationen), optional nur passende."""
    with open(os.path.join(sysroot(), "setupapi.log"), "rb") as f:
        f.seek(0, 2)
        size = f.tell()
        f.seek(max(0, size - 2 * 1024 * 1024))
        text = _decode(f.read())
    blocks = re.split(r"\r?\n(?=\[\d{4}/\d\d/\d\d )", text)
    g = (req.get("grep") or "").lower()
    if g:
        blocks = [b for b in blocks if g in b.lower()]
    return {"ok": True, "blocks": blocks[-(req.get("last") or 5):]}


def op_uninstall(sock, req):
    """Uninstall-Eintrag per Name (Teil von DisplayName oder Schlüssel) finden, mit run den Deinstaller starten
    (MSI: msiexec /x {GUID} /qb). Der Deinstaller läuft losgelöst, Dialoge danach per ui/press."""
    q = req["name"].lower()
    u = reg_read(HKLM_UNINSTALL)
    hits = []
    for key in (u[1] if u else []):
        e = reg_read(HKLM_UNINSTALL + "\\" + key, False)
        if e is None:
            continue
        v = e[0]
        disp = str(v.get("DisplayName") or "")
        if q == key.lower() or q == disp.lower():
            hits = [(key, v)]
            break
        if q in key.lower() or q in disp.lower():
            hits.append((key, v))
    info = [{"key": k, "name": str(v.get("DisplayName") or ""), "version": str(v.get("DisplayVersion") or ""),
             "uninstall": str(v.get("QuietUninstallString") or v.get("UninstallString") or ""),
             "location": str(v.get("InstallLocation") or "")} for k, v in hits]
    if not req.get("run"):
        return {"ok": True, "entries": info}
    if len(info) != 1:
        return {"ok": False, "error": "'%s' passt auf %d Einträge, genauer angeben" % (req["name"], len(info)),
                "entries": info}
    cmd = info[0]["uninstall"]
    if not cmd:
        return {"ok": False, "error": "kein UninstallString", "entries": info}
    m = re.search(r"(?i)msiexec(\.exe)?\s+/[ix]\s*(\{[0-9a-f-]+\})", cmd)
    if m:
        cmd = "msiexec.exe /x %s /qb" % m.group(2)
    subprocess.Popen(cmd, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                     creationflags=0x00000008 | 0x00000200)  # DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
    return {"ok": True, "entries": info, "started": cmd}


def op_ver(sock, req):
    """VERSIONINFO, Größe, Datum, md5 beliebiger Exe/DLL-Dateien."""
    out = {}
    for p in req["paths"]:
        try:
            out[p] = exe_version(p)
        except Exception as e:
            out[p] = {"error": str(e)}
    return {"ok": True, "files": out}


OPS = {"ping": op_ping, "run": op_run, "put": op_put, "get": op_get, "update": op_update,
       "ui": op_ui, "shot": op_shot, "click": op_click, "press": op_press,
       "settext": op_settext, "key": op_key, "type": op_type, "focus": op_focus,
       "close": op_close, "updates": op_updates, "programs": op_programs,
       "sysinfo": op_sysinfo, "reg": op_reg, "settings": op_settings,
       "productkey": op_productkey, "lnk": op_lnk, "desk": op_desk, "deskpos": op_deskpos,
       "mount": op_mount, "eject": op_eject, "vol": op_vol,
       "ripiso": op_ripiso, "pesec": op_pesec, "driver": op_driver,
       "games": op_games, "ls": op_ls, "cat": op_cat, "ps": op_ps, "kill": op_kill,
       "hash": op_hash, "copy": op_copy, "rm": op_rm, "disc": op_disc, "setupapi": op_setupapi,
       "uninstall": op_uninstall, "ver": op_ver}


def describe(req):
    op = req.get("op")
    if op == "run":
        extra = " [detach]" if req.get("detach") else ""
        return "run%s: %s" % (extra, req.get("cmd"))
    if op in ("put", "get"):
        return "%s: %s" % (op, req.get("path"))
    rest = ", ".join("%s=%s" % (k, v) for k, v in sorted(req.items()) if k != "op")
    return "%s %s" % (op, rest) if rest else str(op)


# ---------- Verbindung ----------

def serve_client(sock, addr, key, allow):
    ip = addr[0]
    try:
        if not any(ipaddress.ip_address(ip) in net for net in allow):
            log("ABGELEHNT %s (nicht im erlaubten Netz)" % ip)
            return
        sock.settimeout(10)
        if not handshake(sock, key):
            log("ABGELEHNT %s (falsches Passwort)" % ip)
            return
        sock.settimeout(None)
        while True:
            req = recv_json(sock)
            op = req.get("op")
            if op == "bye":
                break
            handler = OPS.get(op)
            if handler is None:
                send_json(sock, {"ok": False, "error": "unbekannte op: %s" % op})
                continue
            log(describe(req))
            try:
                resp = handler(sock, req)
            except EOFError:
                raise
            except Exception as e:
                resp = {"ok": False, "error": "%s: %s" % (type(e).__name__, e)}
            if not resp.get("ok"):
                log("  Fehler: %s" % resp.get("error"))
            elif op == "run" and "exit" in resp:
                log("  exit=%s%s" % (resp["exit"], " (TIMEOUT)" if resp["timed_out"] else ""))
            send_json(sock, resp)
    except EOFError:
        pass
    except Exception as e:
        log("Verbindungsfehler %s: %s: %s" % (ip, type(e).__name__, e))
    finally:
        try:
            sock.close()
        except Exception:
            pass


def main():
    ap = argparse.ArgumentParser(description="xpserver (sichtbarer Befehls-Server)")
    ap.add_argument("--bind", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=DEFAULT_PORT)
    ap.add_argument("--allow", action="append",
                    help="erlaubtes Netz (CIDR), mehrfach möglich (sonst XPCTL_ALLOW, kommagetrennt, oder Konfiguration)")
    ap.add_argument("--config", help="Konfigurationsdatei (sonst XPCTL_CONFIG oder %s neben xpserver.py)" % CONFIG_NAME)
    ap.add_argument("--key-file", help="Passwort aus Datei statt Eingabe")
    args = ap.parse_args()

    env_allow = [n.strip() for n in os.environ.get("XPCTL_ALLOW", "").split(",") if n.strip()]
    load_config(args.config or os.environ.get("XPCTL_CONFIG") or os.path.join(HERE, CONFIG_NAME))
    nets = args.allow or env_allow or CFG.get("allow") or []
    if not nets:
        print('Kein erlaubtes Netz: --allow, XPCTL_ALLOW oder "allow" in %s setzen, Abbruch.' % CONFIG_NAME)
        return 1
    allow = [ipaddress.ip_network(n, strict=False) for n in nets]

    if args.key_file:
        with open(args.key_file, "rb") as f:
            key = f.read().strip()
    elif os.environ.get("XPCTL_KEY"):
        key = os.environ["XPCTL_KEY"].encode("utf-8")
    else:
        key = getpass.getpass("Passwort (Eingabe unsichtbar): ").strip().encode("utf-8")
    if not key:
        print("Kein Passwort, Abbruch.")
        return 1

    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind((args.bind, args.port))
    srv.listen(5)
    srv.settimeout(1.0)
    log("xpserver v%d lauscht auf %s:%d, erlaubt: %s" % (
        VERSION, args.bind, args.port, ", ".join(str(n) for n in allow)))
    log("Strg+C beendet den Server.")
    if os.path.exists(SELF + ".bak"):
        os.remove(SELF + ".bak")  # neue Version läuft, Rollback nicht mehr nötig
    try:
        while True:
            try:
                conn, addr = srv.accept()
            except socket.timeout:
                continue
            t = threading.Thread(target=serve_client, args=(conn, addr, key, allow))
            t.daemon = True
            t.start()
    except KeyboardInterrupt:
        log("beendet.")
    finally:
        srv.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
