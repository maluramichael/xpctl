"""Need for Speed: Underground (PC, 2003).

Dateien in ``<Common AppData>\\NFS Underground`` (XP: ``C:\\Documents and Settings\\All Users\\
Application Data\\NFS Underground``):

- ``Profiles.usr``: Profilliste. 4 Byte Anzahl, dann je Profil 32 Byte, Name bis zum ersten NUL.
  Die Bytes nach dem Namen sind Speicherreste, sie werden unverändert durchgereicht.
- ``<Name>.cfg``: Grafik und Ton, Text ``SCHLÜSSEL: Wert`` mit CRLF, danach eine Zeile mit der
  Tastenbelegung des Eingabegeräts (wird nicht gedeutet).
- ``<Name>.opt``: Optionen, 749 Byte. Name ab Byte 8, Adler-32 über Byte 4 bis Ende-4, am Ende
  als uint32 little endian.
- ``<Name>0.ugd``, ``<Name>_autosave.ugd``: Karriere, 63123 Byte. Kopfname ab Byte 4, Adler-32 über
  Byte 47 bis Ende-4, am Ende als uint32 little endian.
- ``<Name>0.pro``: Online-Profil mit Zugangsdaten. Wird nie angezeigt.

Geprüft an zwei Profilen einer deutschen Retail-Installation mit ``Speed.exe`` vom 15.01.2004.
Die Prüfsummen stimmen für alle fünf Binärdateien. Dass das Spiel eine von hier geschriebene
``.opt``/``.ugd`` annimmt, ist noch nicht geprüft.
"""
from __future__ import annotations

import re
import struct

from .core import Field, Game, OpaqueFile, Profile, Remote, SaveError, SaveFile, adler32, join

SHELL_FOLDERS = r"HKLM\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders"


# --------------------------------------------------------------------------- Dateiformate

# Schlüssel der .cfg: Beschriftung, bekannte Werte. Nur die Bedeutungen mit verified=True
# sind im Spiel gesehen, der Rest ist Rohwert.
CFG_KEYS = {
    "BPP": ("Farbtiefe (Bit)", {}),
    "RES": ("Auflösung (Index)", {4: "1280x1024"}),
    "PERFORMANCE": ("Leistungsstufe", {}),
    "ENVMAP": ("Umgebungsspiegelung Autos", {}),
    "WORLDENVMAP": ("Umgebungsspiegelung Welt", {}),
    "SHADOW": ("Schatten", {}),
    "HEADLIGHTS": ("Scheinwerfer", {}),
    "CARLIGHTING": ("Auto-Beleuchtung", {}),
    "DAMAGE": ("Schadensmodell sichtbar", {}),
    "CROWD": ("Zuschauer", {}),
    "ROADREFLECTION": ("Straßenspiegelung", {}),
    "FOG": ("Nebel", {}),
    "MOTIONBLUR": ("Bewegungsunschärfe", {}),
    "LIGHTSTREAKS": ("Lichtschlieren", {}),
    "LIGHTGLOW": ("Licht-Glow", {}),
    "ANIMATEDTEXTURE": ("animierte Texturen", {}),
    "PARTICLESYSTEMS": ("Partikel", {}),
    "DEPTHOFFIELD": ("Tiefenschärfe", {}),
    "WORLDLOD": ("Detailstufe Welt", {}),
    "CARLOD": ("Detailstufe Autos", {}),
    "SOUNDPERF": ("Tonqualität", {}),
    "SOUNDENABLE": ("Ton an", {}),
}
_CFG_LINE = re.compile(rb"^([A-Za-z]+): (-?\d+)$")


class NfsuCfg(SaveFile):
    """``<Name>.cfg``: Zeilen ``SCHLÜSSEL: Zahl``. Andere Zeilen (Tastenbelegung, Leerzeilen) bleiben
    Byte für Byte erhalten, ebenso Reihenfolge, Schreibweise und Zeilenenden."""

    kind = "cfg"
    label = "Grafik und Ton"

    def parse(self, data: bytes) -> None:
        self.lines = data.split(b"\r\n")

    def serialize(self) -> bytes:
        return b"\r\n".join(self.lines)

    def _items(self):
        for i, ln in enumerate(self.lines):
            m = _CFG_LINE.match(ln)
            if m:
                yield i, m.group(1).decode("ascii"), int(m.group(2))

    def fields(self) -> list[Field]:
        out = []
        for _, k, v in self._items():
            label, choices = CFG_KEYS.get(k.upper(), (k, {}))
            out.append(Field(k, label, v, "int", writable=True, choices=choices))
        return out

    def set(self, key: str, value: str) -> None:
        try:
            v = int(value)
        except ValueError:
            raise SaveError(f"{key}: Zahl erwartet, nicht {value!r}")
        for i, k, _ in self._items():
            if k.lower() == key.lower():
                self.lines[i] = f"{k}: {v}".encode("ascii")
                return
        raise SaveError(f"{self.name}: kein Schlüssel {key}")


class _Adler(SaveFile):
    """Binärdatei mit Name im Kopf und Adler-32 am Ende über ``[start, Ende-4)``."""

    start = 0
    name_at = 0
    size = 0

    def parse(self, data: bytes) -> None:
        if len(data) < self.start + 4:
            raise SaveError(f"{self.path}: zu kurz ({len(data)} Byte)")
        self.data = bytearray(data)

    def serialize(self) -> bytes:
        return bytes(self.data)

    @property
    def stored(self) -> int:
        return struct.unpack_from("<I", self.data, len(self.data) - 4)[0]

    @property
    def computed(self) -> int:
        return adler32(bytes(self.data[self.start:-4]))

    def head_name(self) -> str:
        raw = bytes(self.data[self.name_at:self.name_at + 16])
        return raw.split(b"\0", 1)[0].decode("latin-1")

    def fields(self) -> list[Field]:
        ok = self.stored == self.computed
        return [
            Field("name", "Name im Kopf", self.head_name(), "text"),
            Field("checksum", "Prüfsumme (Adler-32)", self.stored, "checksum", verified=True,
                  note="stimmt" if ok else f"falsch, richtig wäre 0x{self.computed:08x}"),
        ]

    def problems(self) -> list[str]:
        out = []
        if self.size and len(self.data) != self.size:
            out.append(f"Größe {len(self.data)} statt {self.size} Byte")
        if self.stored != self.computed:
            out.append(f"Prüfsumme 0x{self.stored:08x}, richtig wäre 0x{self.computed:08x}")
        return out

    def repair(self) -> bool:
        if self.stored == self.computed:
            return False
        struct.pack_into("<I", self.data, len(self.data) - 4, self.computed)
        return True


class NfsuOpt(_Adler):
    kind = "opt"
    label = "Optionen"
    start, name_at, size = 4, 8, 749


class NfsuUgd(_Adler):
    kind = "ugd"
    label = "Karriere"
    start, name_at, size = 47, 4, 63123


class NfsuAutosave(NfsuUgd):
    kind = "autosave"
    label = "Karriere (Autosave)"


class NfsuOnline(OpaqueFile):
    kind = "online"
    label = "Online-Profil (Zugangsdaten, wird nicht angezeigt)"
    sensitive = True


class NfsuProfiles(SaveFile):
    """``Profiles.usr``: uint32 Anzahl, dann je 32 Byte pro Profil."""

    kind = "usr"
    label = "Profilliste"
    REC = 32

    def parse(self, data: bytes) -> None:
        self.data = bytes(data)
        n = struct.unpack_from("<I", data, 0)[0] if len(data) >= 4 else 0
        self.names = []
        for i in range(n):
            rec = data[4 + i * self.REC: 4 + (i + 1) * self.REC]
            if len(rec) < self.REC:
                break
            self.names.append(rec.split(b"\0", 1)[0].decode("latin-1"))

    def serialize(self) -> bytes:
        return self.data

    def fields(self) -> list[Field]:
        return [Field("profiles", "Profile", ", ".join(self.names), "text")]

    def problems(self) -> list[str]:
        n = struct.unpack_from("<I", self.data, 0)[0] if len(self.data) >= 4 else 0
        want = 4 + n * self.REC
        return [] if len(self.data) == want else [f"Größe {len(self.data)} statt {want} Byte für {n} Profile"]


# --------------------------------------------------------------------------- Spiel

class NfsUnderground(Game):
    """Alle Versionen. Ohne bekannte Exe nur lesen."""

    id = "nfsu"
    title = "Need for Speed: Underground"
    exe = "Speed.exe"
    notes = [
        "Ein Absturz beim Laden eines Profils lag einmal an einer Grafikeinstellung in der .cfg "
        "(RES 3, ENVMAP 3, MOTIONBLUR 1, LIGHTSTREAKS 1 auf einer GeForce 7800 GTX). "
        "Test: die Werte eines frischen Profils übernehmen.",
    ]

    @classmethod
    def locate(cls, remote: Remote) -> str | None:
        for key in (r"HKLM\Software\EA GAMES\NFS Underground", r"HKLM\Software\Electronic Arts\EA GAMES\NFS Underground"):
            d = remote.reg(key, "Install Dir")
            if d:
                return d.rstrip("\\")
        return None

    def shared_files(self) -> list[str]:
        return ["Profiles.usr"]

    def save_dir(self) -> str:
        base = self.remote.reg(SHELL_FOLDERS, "Common AppData")
        if not base:
            raise SaveError("Common AppData nicht in der Registry")
        return join(base, "NFS Underground")

    def _listing(self) -> dict[str, str]:
        return {e.name.lower(): e.name for e in self.remote.files(self.save_dir())}

    def profiles(self) -> list[str]:
        files = self._listing()
        if "profiles.usr" in files:
            usr = NfsuProfiles(join(self.save_dir(), files["profiles.usr"]),
                               self.remote.read(join(self.save_dir(), files["profiles.usr"])))
            names = list(usr.names)
        else:
            names = []
        # Profile mit .cfg, die (noch) nicht in der Liste stehen, auch zeigen
        for low, real in files.items():
            if low.endswith(".cfg"):
                n = real[:-4]
                if n.lower() not in (x.lower() for x in names):
                    names.append(n)
        return names

    FILES = (
        ("{n}.cfg", NfsuCfg),
        ("{n}.opt", NfsuOpt),
        ("{n}0.ugd", NfsuUgd),
        ("{n}_autosave.ugd", NfsuAutosave),
        ("{n}0.pro", NfsuOnline),
    )

    def load(self, name: str) -> Profile:
        files = self._listing()
        out = []
        real_name = None
        for pattern, cls in self.FILES:
            want = pattern.format(n=name).lower()
            if want in files:
                p = join(self.save_dir(), files[want])
                out.append(cls(p, self.remote.read(p)))
                real_name = real_name or files[want][:len(name)]
        if not out:
            raise SaveError(f"kein Profil {name}. Vorhanden: " + ", ".join(self.profiles()))
        return Profile(real_name or name, out)


class NfsUnderground_20040115(NfsUnderground):
    """Speed.exe vom 15.01.2004, 4.057.802 Byte (deutsche Retail-Fassung, vermutlich Patch 1.4;
    die Patchnummer ist nicht belegt, deshalb trägt die Klasse das Exe-Datum)."""

    version = "Speed.exe 15.01.2004"
    exe_md5 = frozenset({"95a0cba2466306469a33171e34995d05"})
