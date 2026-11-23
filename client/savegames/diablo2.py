"""Diablo II / Lord of Destruction (PC), Charakterdateien ``<Name>.d2s``.

Ordner: Registry ``Save Path`` (HKCU, sonst HKLM) unter ``Software\\Blizzard Entertainment\\Diablo II``,
sonst ``<Installation>\\save``. Zu jedem Charakter gehören weitere ``<Name>.*``-Dateien (``.key``
Tastenbelegung, ``.ma*``/``.map`` Kartenstand); sie werden gesichert, aber nicht gedeutet.

Aufbau von ``.d2s`` (Version 0x60 = Patch 1.10 bis 1.14d):

- Kopf mit festen Offsets: Magic 0xAA55AA55 (0), Version (4), Dateigröße (8), Prüfsumme (12),
  Name (20, 16 Byte), Status (36: Bit 2 Hardcore, 3 gestorben, 5 Expansion, 6 Ladder),
  Fortschritt (37), Klasse (40), Level (43), Zeitstempel (48), Schwierigkeit (168, je Byte:
  Bit 7 aktiv, Bit 0-2 Akt).
- Ab 765 ``gf`` und die Charakterwerte als Bitstrom, niedrigstes Bit zuerst: je 9 Bit Id, dann der
  Wert in fester Breite (Tabelle ``STATS``), Ende mit Id 0x1FF, aufgefüllt auf volle Bytes.
- Danach ``if`` mit 30 Byte Skills, danach ``JM`` und die Gegenstände.
- Prüfsumme: Feld auf 0 setzen, dann für jedes Byte ``s = rotl(s, 1) + byte`` (32 Bit).

Breiten und Klassen aus github.com/nokka/d2s (attributes.go, class.go, status.go). An einem echten
1.14d-Charakter geprüft: Prüfsumme, Kopf, Ende der Werte-Sektion direkt vor ``if``, Level im Kopf
gleich Level-Wert. Werte, die in jener Datei nicht vorkamen, sind nur aus der Quelle übernommen.
"""
from __future__ import annotations

import datetime as _dt
import struct

from .core import Field, Game, OpaqueFile, Profile, Remote, SaveError, SaveFile, join

MAGIC = 0xAA55AA55
VERSION_110 = 0x60
STATS_AT = 765          # "gf"

CLASSES = {0: "Amazone", 1: "Zauberin", 2: "Totenbeschwörer", 3: "Paladin", 4: "Barbar",
           5: "Druide", 6: "Assassine"}

# Id: (Schlüssel, Beschriftung, Bitbreite, Teiler, schreibbar)
STATS = {
    0: ("strength", "Stärke", 10, 1, True),
    1: ("energy", "Energie", 10, 1, True),
    2: ("dexterity", "Geschicklichkeit", 10, 1, True),
    3: ("vitality", "Vitalität", 10, 1, True),
    4: ("statpts", "freie Attributpunkte", 10, 1, True),
    5: ("newskills", "freie Skillpunkte", 8, 1, True),
    6: ("hitpoints", "Leben", 21, 256, False),
    7: ("maxhp", "Leben max.", 21, 256, False),
    8: ("mana", "Mana", 21, 256, False),
    9: ("maxmana", "Mana max.", 21, 256, False),
    10: ("stamina", "Ausdauer", 21, 256, False),
    11: ("maxstamina", "Ausdauer max.", 21, 256, False),
    12: ("level", "Level", 7, 1, False),            # steht doppelt (Kopf Byte 43), nur lesen
    13: ("experience", "Erfahrung", 32, 1, False),
    14: ("gold", "Gold (Inventar)", 25, 1, True),
    15: ("goldbank", "Gold (Truhe)", 25, 1, True),
}
_BY_KEY = {v[0]: k for k, v in STATS.items()}


def checksum(data: bytes) -> int:
    b = bytearray(data)
    b[12:16] = b"\0\0\0\0"
    s = 0
    for x in b:
        s = ((s << 1) | (s >> 31)) & 0xFFFFFFFF
        s = (s + x) & 0xFFFFFFFF
    return s


class _Bits:
    def __init__(self, data: bytes):
        self.data, self.pos = data, 0

    def read(self, n: int) -> int:
        v = 0
        for i in range(n):
            byte = self.pos >> 3
            if byte >= len(self.data):
                raise SaveError("Werte-Sektion läuft über das Dateiende")
            v |= ((self.data[byte] >> (self.pos & 7)) & 1) << i
            self.pos += 1
        return v


def encode_stats(items: list[tuple[int, int]], pad: int = 0) -> bytes:
    """Bitstrom der Werte inkl. Ende-Marke; ``pad`` sind die Füllbits bis zum Byteende."""
    bits = []
    def put(v, n):
        bits.extend((v >> i) & 1 for i in range(n))
    for sid, val in items:
        put(sid, 9)
        put(val, STATS[sid][2])
    put(0x1FF, 9)
    fill = (-len(bits)) % 8
    put(pad, fill)
    out = bytearray(len(bits) // 8)
    for i, b in enumerate(bits):
        out[i >> 3] |= b << (i & 7)
    return bytes(out)


class D2s(SaveFile):
    kind = "d2s"
    label = "Charakter"

    def parse(self, data: bytes) -> None:
        self.data = bytearray(data)
        self.stats: list[tuple[int, int]] | None = None
        self.stats_error = ""
        if len(data) < STATS_AT + 2:
            raise SaveError(f"{self.path}: zu kurz für eine .d2s ({len(data)} Byte)")
        magic, self.version = struct.unpack_from("<II", data, 0)
        if magic != MAGIC:
            raise SaveError(f"{self.path}: keine .d2s (Magic 0x{magic:08x})")
        if self.version != VERSION_110:
            self.stats_error = f"Dateiversion 0x{self.version:02x} unbekannt (erwartet 0x60 = 1.10 bis 1.14d)"
            return
        if data[STATS_AT:STATS_AT + 2] != b"gf":
            self.stats_error = "keine Werte-Sektion bei Byte 765"
            return
        try:
            br = _Bits(bytes(data[STATS_AT + 2:]))
            items = []
            while True:
                sid = br.read(9)
                if sid == 0x1FF:
                    break
                if sid not in STATS:
                    raise SaveError(f"unbekannte Werte-Id {sid}")
                items.append((sid, br.read(STATS[sid][2])))
            fill = (-br.pos) % 8
            self.pad = br.read(fill)
            self.stats_len = br.pos // 8
            end = STATS_AT + 2 + self.stats_len
            if data[end:end + 2] != b"if":
                raise SaveError(f"nach den Werten folgt nicht 'if' (Byte {end})")
            self.stats = items
        except SaveError as e:
            self.stats_error = str(e)

    def serialize(self) -> bytes:
        return bytes(self.data)

    # --- Kopf

    @property
    def char_name(self) -> str:
        return bytes(self.data[20:36]).split(b"\0", 1)[0].decode("latin-1")

    @property
    def stored(self) -> int:
        return struct.unpack_from("<I", self.data, 12)[0]

    def _stat(self, key: str) -> int | None:
        sid = _BY_KEY[key]
        return next((v for i, v in (self.stats or []) if i == sid), None)

    @property
    def writable(self) -> bool:
        return self.stats is not None

    def fields(self) -> list[Field]:
        d = self.data
        st = d[36]
        flags = [n for bit, n in ((2, "Hardcore"), (3, "gestorben"), (5, "Expansion"), (6, "Ladder")) if st >> bit & 1]
        diff = []
        for i, n in enumerate(("Normal", "Albtraum", "Hölle")):
            b = d[168 + i]
            if b & 0x80:
                diff.append(f"{n} Akt {(b & 7) + 1} (aktiv)")
        ts = struct.unpack_from("<I", d, 48)[0]
        when = _dt.datetime.fromtimestamp(ts).strftime("%Y-%m-%d %H:%M") if ts else "-"
        ok = self.stored == checksum(d)
        out = [
            Field("name", "Name", self.char_name, "text", verified=True),
            Field("class", "Klasse", d[40], choices=CLASSES, verified=True),
            Field("headlevel", "Level (Kopf)", d[43], verified=True),
            Field("status", "Status", ", ".join(flags) or "-", "text", verified=True),
            Field("progression", "Fortschritt (Rohwert)", d[37]),
            Field("difficulty", "Schwierigkeit", ", ".join(diff) or "-", "text"),
            Field("saved", "zuletzt gespeichert", when, "text"),
            Field("version", "Dateiversion", f"0x{self.version:02x}", "text", verified=True),
            Field("checksum", "Prüfsumme", self.stored, "checksum", verified=True,
                  note="stimmt" if ok else f"falsch, richtig wäre 0x{checksum(d):08x}"),
        ]
        if self.stats is None:
            out.append(Field("stats", "Werte", "nicht lesbar", "text", note=self.stats_error))
            return out
        present = {i: v for i, v in self.stats}
        for sid, (key, label, _, div, w) in STATS.items():
            if sid not in present:
                if w:   # zeigen, damit set eine verständliche Meldung geben kann
                    out.append(Field(key, label, 0, writable=True,
                                     note="steht nicht in der Datei (0), erst im Spiel einmal ändern"))
                continue
            v = present[sid]
            shown = v // div if div > 1 else v
            note = "" if div == 1 else "Festkomma /256, gerundet"
            out.append(Field(key, label, shown, writable=w, note=note))
        return out

    def set(self, key: str, value: str) -> None:
        if self.stats is None:
            raise SaveError(f"{self.name}: Werte nicht lesbar ({self.stats_error}), nur lesen")
        k = key.lower()
        if k not in _BY_KEY or not STATS[_BY_KEY[k]][4]:
            raise SaveError(f"{self.name}: {key} ist nicht schreibbar")
        sid = _BY_KEY[k]
        try:
            v = int(value)
        except ValueError:
            raise SaveError(f"{key}: Zahl erwartet, nicht {value!r}")
        width = STATS[sid][2]
        if not 0 <= v < (1 << width):
            raise SaveError(f"{key}: 0 bis {(1 << width) - 1} erlaubt")
        if k == "gold" and v > self.data[43] * 10000:
            raise SaveError(f"gold: im Inventar höchstens Level x 10.000 = {self.data[43] * 10000}")
        if k == "goldbank" and v > 2_500_000:
            raise SaveError("goldbank: höchstens 2.500.000")
        if sid not in dict(self.stats):
            # Ein fehlender Wert verschiebt alles danach und die Dateigröße; noch nicht an echten Dateien geprüft.
            raise SaveError(f"{key} steht noch nicht in der Datei (Wert 0, das Spiel lässt ihn dann weg). "
                            "Erst im Spiel einmal ändern (z. B. Gold aufheben), dann geht es hier.")
        old_len = self.stats_len
        self.stats = [(i, v if i == sid else x) for i, x in self.stats]
        enc = encode_stats(self.stats, self.pad)
        if len(enc) != old_len:          # gleiche Breiten, gleiche Länge; Absicherung
            raise SaveError("interner Fehler: Länge der Werte-Sektion würde sich ändern")
        self.data[STATS_AT + 2:STATS_AT + 2 + old_len] = enc
        struct.pack_into("<I", self.data, 12, checksum(self.data))

    def problems(self) -> list[str]:
        out = []
        size = struct.unpack_from("<I", self.data, 8)[0]
        if size != len(self.data):
            out.append(f"Dateigröße im Kopf {size}, Datei hat {len(self.data)} Byte (abgeschnitten?)")
        if self.stored != checksum(self.data):
            out.append(f"Prüfsumme 0x{self.stored:08x}, richtig wäre 0x{checksum(self.data):08x}")
        if self.stats is not None and self._stat("level") not in (None, self.data[43]):
            out.append(f"Level im Kopf {self.data[43]}, in den Werten {self._stat('level')}")
        return out

    def repair(self) -> bool:
        c = checksum(self.data)
        if self.stored == c:
            return False
        struct.pack_into("<I", self.data, 12, c)
        return True


class DiabloII(Game):
    """Alle Versionen. Ohne bekannte Game.exe nur lesen."""

    id = "d2"
    title = "Diablo II"
    exe = "Game.exe"
    processes = ("Game.exe", "Diablo II.exe")
    notes = ["Game.exe heißen auch andere Spiele (z. B. Driver, Mafia, Nox): läuft eins davon, "
             "verweigert saves das Schreiben vorsichtshalber ebenfalls."]
    KEY = r"Software\Blizzard Entertainment\Diablo II"

    @classmethod
    def locate(cls, remote: Remote) -> str | None:
        for hive in ("HKCU", "HKLM"):
            d = remote.reg(f"{hive}\\{cls.KEY}", "InstallPath")
            if d:
                return d.rstrip("\\")
        return None

    def save_dir(self) -> str:
        for hive in ("HKCU", "HKLM"):
            d = self.remote.reg(f"{hive}\\{self.KEY}", "Save Path")
            if d:
                return d.rstrip("\\")
        return join(self.install_dir or "", "save")

    def profiles(self) -> list[str]:
        return sorted((e.name[:-4] for e in self.remote.files(self.save_dir()) if e.name.lower().endswith(".d2s")),
                      key=str.lower)

    def load(self, name: str) -> Profile:
        entries = self.remote.files(self.save_dir())
        mine = [e.name for e in entries if e.name.rsplit(".", 1)[0].lower() == name.lower()]
        d2s = [n for n in mine if n.lower().endswith(".d2s")]
        if not d2s:
            raise SaveError(f"kein Charakter {name}. Vorhanden: " + ", ".join(self.profiles()))
        files: list[SaveFile] = []
        for n in sorted(mine, key=lambda x: (not x.lower().endswith(".d2s"), x.lower())):
            p = join(self.save_dir(), n)
            files.append((D2s if n.lower().endswith(".d2s") else OpaqueFile)(p, self.remote.read(p)))
        return Profile(d2s[0][:-4], files)


class DiabloII_114d(DiabloII):
    """Lord of Destruction 1.14d, Game.exe 1.14.3.71 (3.618.792 Byte, 31.05.2016)."""

    version = "1.14d (Game.exe 1.14.3.71)"
    exe_md5 = frozenset({"2b3600c10ee0d6387675a2f6cf0ebed2"})
