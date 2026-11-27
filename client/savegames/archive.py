"""Spielstände als normale Zip-Dateien sichern, ein Zip pro Spiel und Zeitpunkt.

Ablage: ``<ziel>/<spiel>/<spiel>_YYYY-MM-DD_HH-MM-SS.zip`` in der Ortszeit des Clients. Alphabetisch
sortiert ist das chronologisch, der Name enthält keine Doppelpunkte (Windows).

Inhalt: alle Dateien des Spielstand-Ordners samt Unterordnern, Pfade relativ zu diesem Ordner, dazu
``info.json`` mit Spiel, Version, Originalordner, Zeitpunkt mit UTC-Abstand und pro Datei Größe,
Änderungszeit und md5. Jedes Zip-Werkzeug öffnet die Datei, zurückspielen geht auch von Hand.

Ein neues Zip entsteht nur, wenn sich die Dateien gegenüber dem letzten Zip unterscheiden (Vergleich
der md5 der Zip-Einträge, nicht von info.json). Ein vorhandenes Zip wird nie überschrieben.
"""
from __future__ import annotations

import datetime as _dt
import hashlib
import json
import os
import re
import zipfile
from dataclasses import dataclass, field
from pathlib import Path

from .core import Game, SaveError, join

INFO = "info.json"


@dataclass
class Snapshot:
    game: str
    status: str                   # neu | unverändert | leer
    path: Path | None = None      # das neue Zip
    previous: Path | None = None  # letztes Zip vorher
    files: list[str] = field(default_factory=list)
    size: int = 0                 # Summe der Dateien
    running: list[str] = field(default_factory=list)


def archive_name(game_id: str, when: _dt.datetime) -> str:
    return f"{game_id}_{when:%Y-%m-%d_%H-%M-%S}.zip"


def archives(dest_root: str | Path, game_id: str) -> list[Path]:
    """Vorhandene Zips eines Spiels, älteste zuerst. Andere Dateien im Ordner zählen nicht."""
    d = Path(dest_root) / game_id
    pat = re.compile(rf"^{re.escape(game_id)}_\d{{4}}-\d{{2}}-\d{{2}}_\d{{2}}-\d{{2}}-\d{{2}}\.zip$")
    if not d.is_dir():
        return []
    return sorted(p for p in d.iterdir() if p.is_file() and pat.match(p.name))


def zip_md5s(path: Path) -> dict[str, str]:
    """md5 jedes Eintrags außer info.json."""
    with zipfile.ZipFile(path) as z:
        return {n: hashlib.md5(z.read(n)).hexdigest() for n in z.namelist() if n != INFO and not n.endswith("/")}


def _zip_time(mtime: str, fallback: _dt.datetime) -> tuple:
    """Server-mtime ``yyyy-MM-dd HH:mm`` (Ortszeit des XP-Rechners), Sekunden fehlen dort."""
    try:
        t = _dt.datetime.strptime(mtime, "%Y-%m-%d %H:%M")
        if t.year >= 1980:                       # früher kann Zip nicht
            return t.timetuple()[:6]
    except (TypeError, ValueError):
        pass
    return fallback.timetuple()[:6]


def _running(game: Game) -> list[str]:
    out = []
    for proc in game.processes or (game.exe,):
        try:
            if game.remote.running(proc):
                out.append(proc)
        except SaveError:
            out.append(f"{proc}?")
    return out


def snapshot(game: Game, dest_root: str | Path, now: _dt.datetime | None = None) -> Snapshot:
    """Sichert den Spielstand-Ordner von ``game`` als neues Zip unter ``dest_root/<id>/``."""
    folder = game.save_dir()
    entries = game.remote.tree(folder)
    if not entries:
        return Snapshot(game.id, "leer")
    when = (now or _dt.datetime.now()).astimezone()
    members = []
    for rel, e in sorted(entries, key=lambda x: x[0].lower()):
        data = game.remote.read(join(folder, rel))
        members.append((rel.replace("\\", "/"), data, e))
    md5s = {name: hashlib.md5(data).hexdigest() for name, data, _ in members}
    names = [m[0] for m in members]
    size = sum(len(m[1]) for m in members)
    old = archives(dest_root, game.id)
    prev = old[-1] if old else None
    if prev is not None and zip_md5s(prev) == md5s:
        return Snapshot(game.id, "unverändert", previous=prev, files=names, size=size)

    running = _running(game)
    info = {
        "game": game.id,
        "title": game.title,
        "version": game.version_text,
        "exe": game.exe,
        "exe_md5": game.md5,
        "install_dir": game.install_dir,
        "save_dir": folder,
        "created": when.isoformat(timespec="seconds"),
        "created_by": "xpctl saves backup",
        "running": running,
        "files": [{"path": name, "size": len(data), "mtime": e.mtime, "md5": md5s[name]}
                  for name, data, e in members],
    }
    d = Path(dest_root) / game.id
    d.mkdir(parents=True, exist_ok=True)
    final = d / archive_name(game.id, when)
    if final.exists():
        raise SaveError(f"{final} gibt es schon (gleiche Sekunde, anderer Inhalt). In einer Sekunde noch einmal.")
    part = final.with_name(final.name + ".part")
    if part.exists():
        part.unlink()           # Rest eines abgebrochenen Laufs, nie ein gültiges Zip
    try:
        with open(part, "xb") as fh:
            os.chmod(part, 0o600)   # NFSU *0.pro enthält Online-Zugangsdaten
            with zipfile.ZipFile(fh, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
                for name, data, e in members:
                    zi = zipfile.ZipInfo(name, _zip_time(e.mtime, when))
                    zi.compress_type = zipfile.ZIP_DEFLATED
                    zi.external_attr = 0o600 << 16
                    z.writestr(zi, data, compresslevel=9)
                zi = zipfile.ZipInfo(INFO, when.timetuple()[:6])
                zi.compress_type = zipfile.ZIP_DEFLATED
                zi.external_attr = 0o600 << 16
                z.writestr(zi, json.dumps(info, ensure_ascii=False, indent=1) + "\n")
        with zipfile.ZipFile(part) as z:
            bad = z.testzip()
        if bad is not None:
            raise SaveError(f"{part}: Eintrag {bad} ist beim Prüfen kaputt")
        if zip_md5s(part) != md5s:
            raise SaveError(f"{part}: Inhalt beim Zurücklesen anders als gelesen")
        if final.exists():
            raise SaveError(f"{final} ist während des Sicherns entstanden, nichts überschrieben")
        os.rename(part, final)
    finally:
        if part.exists():
            part.unlink()
    return Snapshot(game.id, "neu", path=final, previous=prev, files=names, size=size, running=running)
