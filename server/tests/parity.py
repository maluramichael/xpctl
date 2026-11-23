#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.9"
# ///
"""Vergleicht zwei xpserver (z. B. alter Python-Server auf 5050, .NET-Server auf 5051).

Schickt jede Anfrage an beide Server und vergleicht die JSON-Antworten feldgenau.
Datenströme (get, shot, ripiso ...) werden mitgelesen und per sha256 verglichen.

  parity.py --host 192.168.1.50 --a 5050 --b 5051 cases/core.jsonl
  parity.py --host ... '{"op": "ls", "path": "C:\\\\xpctl"}'

Fälle: eine JSON-Anfrage pro Zeile, Zeilen mit # sind Kommentare. Ein Fall darf
"_ignore": ["feld", "liste.*.feld"] enthalten (wird nicht gesendet). Pfade gelten exakt
ab der Wurzel, "*" steht für genau eine Ebene (Listenindex oder Schlüssel).
Passwort wie beim Client (XPCTL_KEY oder ~/.config/xpctl/key). Exit 1 bei Abweichung.
"""
import argparse
import hashlib
import importlib.util
import json
import os
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("xpctl", HERE.parent.parent / "client" / "xpctl.py")
xp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(xp)

# Felder, die sich zwischen den beiden Servern zwangsläufig unterscheiden. Gilt nur für ping, nur auf oberster Ebene.
PING_IGNORE = ["time", "script", "cwd", "version", "runtime", "kind", "python", "supervised", "sha256"]


def call(host, port, key, req):
    s = xp.connect(host, port, key)
    try:
        xp.send_json(s, req)
        r = xp.recv_json(s)
        stream = None
        if r.get("ready") and "size" in r and req.get("op") not in ("put", "update"):
            h, got = hashlib.sha256(), 0
            while got < int(r["size"]):
                d = xp.recv_frame(s)
                h.update(d)
                got += len(d)
            stream = h.hexdigest()
            r2 = xp.recv_json(s)
            r = {"head": r, "tail": r2, "stream_sha256": stream}
        xp.send_json(s, {"op": "bye"})
        return r
    finally:
        s.close()


def match(path, patterns):
    for p in patterns:
        a, b = p.split("."), path.split(".")
        if len(a) == len(b) and all(x == "*" or x == y for x, y in zip(a, b)):
            return True

    return False


def diff(a, b, path, ignore, out):
    if path and match(path, ignore):
        return
    if isinstance(a, dict) and isinstance(b, dict):
        for k in list(a.keys()) + [k for k in b if k not in a]:
            p = f"{path}.{k}" if path else k
            if k not in a:
                if not match(p, ignore):
                    out.append(f"  {p}: fehlt in A, B={short(b[k])}")
            elif k not in b:
                if not match(p, ignore):
                    out.append(f"  {p}: fehlt in B, A={short(a[k])}")
            else:
                diff(a[k], b[k], p, ignore, out)
        return
    if isinstance(a, list) and isinstance(b, list):
        if len(a) != len(b):
            out.append(f"  {path}: Länge A={len(a)} B={len(b)}")
        for i, (x, y) in enumerate(zip(a, b)):
            diff(x, y, f"{path}.{i}", ignore, out)
        return
    if type(a) != type(b) and not (isinstance(a, (int, float)) and isinstance(b, (int, float)) and not isinstance(a, bool) and not isinstance(b, bool)):
        out.append(f"  {path}: Typ A={type(a).__name__}:{short(a)} B={type(b).__name__}:{short(b)}")
    elif a != b:
        out.append(f"  {path}: A={short(a)} B={short(b)}")


def short(v, n=160):
    s = json.dumps(v, ensure_ascii=False)
    return s if len(s) <= n else s[:n] + "..."


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default=os.environ.get("XPCTL_HOST"))
    ap.add_argument("--a", type=int, default=5050, help="Port Referenz (Python)")
    ap.add_argument("--b", type=int, default=5051, help="Port Kandidat (.NET)")
    ap.add_argument("--max", type=int, default=40, help="höchstens so viele Abweichungen je Fall zeigen")
    ap.add_argument("cases", nargs="+", help="Datei(en) mit Fällen oder JSON-Anfrage")
    args = ap.parse_args()
    key = xp.load_key()
    reqs = []
    for c in args.cases:
        if c.lstrip().startswith("{"):
            reqs.append(json.loads(c))
            continue
        for ln in Path(c).read_text(encoding="utf-8").splitlines():
            if ln.strip() and not ln.lstrip().startswith("#"):
                reqs.append(json.loads(ln))
    bad = 0
    for req in reqs:
        ign = set(req.pop("_ignore", [])) | (set(PING_IGNORE) if req.get("op") == "ping" else set())
        try:
            ra = call(args.host, args.a, key, req)
        except Exception as e:
            ra = {"_exception": repr(e)}
        try:
            rb = call(args.host, args.b, key, req)
        except Exception as e:
            rb = {"_exception": repr(e)}
        out = []
        diff(ra, rb, "", sorted(ign), out)
        label = json.dumps(req, ensure_ascii=False)
        if out:
            bad += 1
            print(f"ABWEICHUNG {label}")
            for ln in out[:args.max]:
                print(ln)
            if len(out) > args.max:
                print(f"  ... {len(out) - args.max} weitere")
        else:
            print(f"gleich     {label}")
    print(f"{len(reqs) - bad}/{len(reqs)} gleich")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
