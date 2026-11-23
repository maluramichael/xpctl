"""Gemeinsame Bausteine für Spielstände: Zugriff auf den Rechner, Felder, Dateien, Profile, Spiele.

Aufbau
------
- ``Remote``: Dateizugriff auf den XP-Rechner. ``xpctl.py`` liefert die echte Umsetzung über
  xpserver, die Tests eine im Speicher.
- ``SaveFile``: eine Datei. ``parse`` liest die Bytes, ``serialize`` erzeugt sie wieder, Bytes, die das
  Format nicht kennt, bleiben unverändert. ``fields`` zeigt, was das Format versteht, ``set`` ändert ein
  Feld, ``problems`` und ``repair`` kümmern sich um Prüfsummen.
- ``Profile``: alle Dateien eines Spielerprofils.
- ``Game``: ein Spiel. Pro Spiel gibt es eine Basisklasse (z. B. ``NfsUnderground``) mit allem, was
  für jede Version gilt, und pro bekannter Exe-Version eine Unterklasse, deren Name die Version trägt
  (z. B. ``NfsUnderground_20040115``). Welche Klasse gilt, entscheidet die md5 der Exe. Kennt keine
  Unterklasse die Exe, nimmt ``resolve`` die Basisklasse und die darf nur lesen.
"""
from __future__ import annotations

import datetime as _dt
import zlib
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import Iterable


class SaveError(Exception):
    """Fehler, den der Nutzer lesen soll (kein Stacktrace)."""


# --------------------------------------------------------------------------- Rechnerzugriff

@dataclass
class Entry:
    name: str
    size: int
    mtime: str


class Remote(ABC):
    """Was ein Spielstand-Format vom Zielrechner braucht. Pfade sind Windows-Pfade."""

    @abstractmethod
    def read(self, path: str) -> bytes: ...

    @abstractmethod
    def write(self, path: str, data: bytes, mkdir: bool = False) -> None: ...

    @abstractmethod
    def files(self, folder: str) -> list[Entry]:
        """Dateien (keine Ordner) direkt in ``folder``; leer, wenn es den Ordner nicht gibt."""

    @abstractmethod
    def md5(self, path: str) -> str | None:
        """md5 einer Datei, None wenn es sie nicht gibt."""

    @abstractmethod
    def running(self, exe_name: str) -> bool:
        """Läuft ein Prozess mit diesem Exe-Namen (ohne Pfad, Groß/klein egal)?"""

    @abstractmethod
    def reg(self, key: str, name: str) -> str | None:
        """Ein Registry-Wert als Text, None wenn Schlüssel oder Wert fehlen."""


def join(*parts: str) -> str:
    return "\\".join(p.strip("\\") if i else p.rstrip("\\") for i, p in enumerate(parts) if p)


# --------------------------------------------------------------------------- Felder

@dataclass
class Field:
    """Ein Wert, den ein Format versteht.

    ``verified``: im Spiel geprüft (z. B. Wert geändert und das Spiel zeigt ihn so an).
    ``choices``: bekannte Bedeutungen einzelner Werte; fehlende Werte sind nicht unbedingt ungültig.
    """
    key: str
    label: str
    value: object
    kind: str = "int"            # int, text, checksum
    writable: bool = False
    verified: bool = False
    choices: dict = field(default_factory=dict)
    note: str = ""

    def shown(self) -> str:
        v = self.value
        if self.kind == "checksum":
            return f"0x{v:08x}" if isinstance(v, int) else str(v)
        meaning = self.choices.get(v)
        return f"{v} ({meaning})" if meaning else str(v)


# --------------------------------------------------------------------------- Dateien

class SaveFile(ABC):
    """Eine Datei eines Spielstands. Unterklassen setzen ``kind`` (kurzer Name, z. B. ``cfg``)."""

    kind = "datei"
    label = "Datei"
    sensitive = False            # Inhalt nie anzeigen (z. B. Online-Zugangsdaten)

    def __init__(self, path: str, data: bytes):
        self.path = path
        self.original = bytes(data)
        self.parse(self.original)

    @property
    def name(self) -> str:
        return self.path.rsplit("\\", 1)[-1]

    @abstractmethod
    def parse(self, data: bytes) -> None: ...

    @abstractmethod
    def serialize(self) -> bytes: ...

    def fields(self) -> list[Field]:
        return []

    def set(self, key: str, value: str) -> None:
        raise SaveError(f"{self.name}: Feld {key} ist nicht schreibbar")

    def problems(self) -> list[str]:
        return []

    def repair(self) -> bool:
        """Behebt, was ``problems`` meldet (z. B. Prüfsumme neu rechnen). True, wenn sich etwas ändert."""
        return False

    @property
    def dirty(self) -> bool:
        return self.serialize() != self.original


class OpaqueFile(SaveFile):
    """Datei, deren Inhalt das Format nicht deutet; wird nur durchgereicht."""

    kind = "roh"
    label = "nicht gedeutet, wird mitgesichert"

    def parse(self, data: bytes) -> None:
        self.data = data

    def serialize(self) -> bytes:
        return self.data


def adler32(data: bytes) -> int:
    return zlib.adler32(data) & 0xFFFFFFFF


# --------------------------------------------------------------------------- Profile

@dataclass
class Profile:
    name: str
    files: list[SaveFile]

    def file(self, kind: str) -> SaveFile | None:
        return next((f for f in self.files if f.kind == kind), None)

    def fields(self) -> list[tuple[SaveFile, Field]]:
        return [(f, x) for f in self.files if not f.sensitive for x in f.fields()]

    def find(self, key: str) -> tuple[SaveFile, Field]:
        """``datei.FELD`` oder nur ``FELD``, wenn der Name eindeutig ist. Groß/klein egal."""
        want = key.lower()
        hits = [(f, x) for f, x in self.fields()
                if want in (x.key.lower(), f"{f.kind}.{x.key}".lower(), f"{f.name}.{x.key}".lower())]
        if not hits:
            raise SaveError(f"kein Feld {key} in Profil {self.name}")
        if len(hits) > 1:
            raise SaveError(f"{key} ist mehrdeutig: " + ", ".join(f"{f.kind}.{x.key}" for f, x in hits))
        return hits[0]

    def set(self, key: str, value: str) -> None:
        f, x = self.find(key)
        if not x.writable:
            raise SaveError(f"{f.kind}.{x.key} ist nicht schreibbar")
        f.set(x.key, value)

    def problems(self) -> list[tuple[SaveFile, str]]:
        return [(f, p) for f in self.files for p in f.problems()]

    def dirty(self) -> list[SaveFile]:
        return [f for f in self.files if f.dirty]


# --------------------------------------------------------------------------- Spiele

_REGISTRY: list[type["Game"]] = []


class Game(ABC):
    """Basisklasse eines Spiels. Unterklassen ohne ``exe_md5`` sind Spielfamilien (nur lesen),
    Unterklassen mit ``exe_md5`` sind geprüfte Versionen (lesen und schreiben)."""

    id = ""                      # Kurzname für die Kommandozeile, z. B. "nfsu"
    title = ""
    exe = ""                     # Exe-Name im Installationsordner (für die md5)
    processes: tuple[str, ...] = ()  # Prozesse, die beim Schreiben nicht laufen dürfen (Standard: exe)
    version = ""                 # lesbare Version, nur bei Versionsklassen
    exe_md5: frozenset[str] = frozenset()
    notes: list[str] = []

    def __init_subclass__(cls, **kw):
        super().__init_subclass__(**kw)
        _REGISTRY.append(cls)

    def __init__(self, remote: Remote, install_dir: str | None, md5: str | None):
        self.remote = remote
        self.install_dir = install_dir
        self.md5 = md5

    # --- Einordnung

    @classmethod
    def family(cls) -> type["Game"]:
        """Die Basisklasse des Spiels (erste Klasse der Kette mit eigener ``id``)."""
        fam = cls
        for k in cls.__mro__:
            if isinstance(k, type) and issubclass(k, Game) and k is not Game and k.__dict__.get("id"):
                fam = k
        return fam

    @property
    def writable(self) -> bool:
        return bool(self.exe_md5) and self.md5 in self.exe_md5

    @property
    def version_text(self) -> str:
        if self.writable:
            return f"{self.version} ({type(self).__name__})"
        if self.md5:
            return f"unbekannte Exe (md5 {self.md5[:12]}), nur lesen"
        return "Exe nicht gefunden, nur lesen"

    # --- von Spielen umzusetzen

    @classmethod
    @abstractmethod
    def locate(cls, remote: Remote) -> str | None:
        """Installationsordner oder None."""

    @abstractmethod
    def save_dir(self) -> str: ...

    @abstractmethod
    def profiles(self) -> list[str]: ...

    @abstractmethod
    def load(self, name: str) -> Profile: ...

    # --- gemeinsam

    def store(self, profile: Profile, backup_root: str) -> tuple[str, list[str]]:
        """Schreibt geänderte Dateien. Vorher: Version geprüft, Spiel läuft nicht, Sicherung angelegt.
        Danach wird jede Datei zurückgelesen und verglichen. Gibt (Sicherungsordner, Pfade) zurück."""
        if not self.writable:
            raise SaveError(f"{self.title}: {self.version_text}. Schreiben nur für geprüfte Versionen.")
        for proc in self.processes or (self.exe,):
            if self.remote.running(proc):
                raise SaveError(f"{proc} läuft. Erst das Spiel beenden, es überschreibt die Dateien sonst "
                                f"(heißt ein anderes Programm genauso, ebenfalls beenden).")
        changed = profile.dirty()
        if not changed:
            return "", []
        bdir = self._fresh_backup_dir(backup_root)
        for f in changed:
            self.remote.write(join(bdir, f.name), f.original, mkdir=True)
        written = []
        for f in changed:
            data = f.serialize()
            self.remote.write(f.path, data)
            if self.remote.read(f.path) != data:
                raise SaveError(f"{f.path}: Zurücklesen ergibt andere Bytes. Sicherung liegt in {bdir}")
            f.original = data
            written.append(f.path)
        return bdir, written

    def _fresh_backup_dir(self, backup_root: str) -> str:
        """Neuer, leerer Sicherungsordner. Eine vorhandene Sicherung wird nie überschrieben."""
        base = join(backup_root, self.id, _dt.datetime.now().strftime("%Y%m%d-%H%M%S"))
        bdir, n = base, 1
        while self.remote.files(bdir):
            n += 1
            bdir = f"{base}-{n}"
        return bdir

    def shared_files(self) -> list[str]:
        """Dateinamen im Spielstand-Ordner, die zu allen Profilen gehören (z. B. eine Profilliste)."""
        return []

    def backup(self, names: Iterable[str] | None, backup_root: str) -> tuple[str, list[str]]:
        """Kopiert Dateien unverändert in einen neuen Sicherungsordner. Ohne Namen: alles im
        Spielstand-Ordner. Mit Namen: die Dateien dieser Profile plus ``shared_files``."""
        bdir = self._fresh_backup_dir(backup_root)
        folder = self.save_dir()
        present = {e.name.lower(): e.name for e in self.remote.files(folder)}
        if names is None:
            wanted = sorted(present.values(), key=str.lower)
        else:
            wanted = [f.name for n in names for f in self.load(n).files]
            wanted += [present[s.lower()] for s in self.shared_files() if s.lower() in present]
        for name in dict.fromkeys(wanted):
            self.remote.write(join(bdir, name), self.remote.read(join(folder, name)), mkdir=True)
        return bdir, list(dict.fromkeys(wanted))


def games() -> list[type[Game]]:
    """Alle Spielfamilien (Basisklassen), je einmal."""
    out = []
    for k in _REGISTRY:
        fam = k.family()
        if fam not in out and fam.id:
            out.append(fam)
    return out


def versions(family: type[Game]) -> list[type[Game]]:
    return [k for k in _REGISTRY if k is not family and issubclass(k, family) and k.exe_md5]


def find_game(game_id: str) -> type[Game]:
    for g in games():
        if g.id == game_id.lower():
            return g
    raise SaveError(f"unbekanntes Spiel {game_id}. Bekannt: " + ", ".join(g.id for g in games()))


def resolve(remote: Remote, family: type[Game]) -> Game | None:
    """Sucht das Spiel auf dem Rechner und wählt die Versionsklasse per md5 der Exe."""
    inst = family.locate(remote)
    if inst is None:
        return None
    md5 = remote.md5(join(inst, family.exe))
    for v in versions(family):
        if md5 and md5 in v.exe_md5:
            return v(remote, inst, md5)
    return family(remote, inst, md5)
