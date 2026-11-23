"""Tests für savegames.diablo2 mit erzeugten .d2s-Dateien (Bitstrom hier unabhängig vom Modul gebaut)."""
from __future__ import annotations

import struct
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from savegames import SaveError, find_game, resolve  # noqa: E402
from savegames.diablo2 import D2s, DiabloII, DiabloII_114d, resolve_save_path  # noqa: E402
from test_savegames import FakeRemote  # noqa: E402

D2_MD5 = "2b3600c10ee0d6387675a2f6cf0ebed2"
INSTALL = r"C:\Games\Diablo II"
SAVE = INSTALL + r"\save"
WIDTH = {0: 10, 1: 10, 2: 10, 3: 10, 4: 10, 5: 8, 6: 21, 7: 21, 8: 21, 9: 21, 10: 21, 11: 21,
         12: 7, 13: 32, 14: 25, 15: 25}


def bits_lsb(pairs: list[tuple[int, int]]) -> bytes:
    """Unabhängiger Bit-Schreiber: niedrigstes Bit zuerst, 9 Bit Id, Wert, Ende 0x1FF, Nullen auffüllen."""
    acc, n = 0, 0
    for sid, val in pairs + [(0x1FF, None)]:
        acc |= sid << n
        n += 9
        if val is not None:
            acc |= val << n
            n += WIDTH[sid]
    return acc.to_bytes((n + 7) // 8, "little")


def rotl_sum(b: bytearray) -> int:
    s = 0
    for x in b:
        s = ((s << 1 | s >> 31) + x) & 0xFFFFFFFF
    return s


def d2s(name=b"Testheld", cls=1, level=5, version=0x60, stats=None, status=0x20) -> bytes:
    stats = stats if stats is not None else [(0, 15), (1, 35), (2, 25), (3, 10), (6, 40 * 256), (12, level), (13, 900), (14, 1234)]
    b = bytearray(765)
    struct.pack_into("<II", b, 0, 0xAA55AA55, version)
    b[20:20 + len(name)] = name
    b[36], b[40], b[43] = status, cls, level
    b[168] = 0x80                          # Normal aktiv, Akt 1
    b += b"gf" + bits_lsb(stats) + b"if" + bytes(30) + b"JM" + b"\0\0"
    struct.pack_into("<I", b, 8, len(b))
    struct.pack_into("<I", b, 12, rotl_sum(b))
    return bytes(b)


class D2Remote(FakeRemote):
    def __init__(self, files, md5=D2_MD5, running_names=()):
        super().__init__(files)
        self._d2md5, self._names = md5, {n.lower() for n in running_names}

    def md5(self, path):
        return self._d2md5 if path.lower().endswith("\\game.exe") else None

    def running(self, exe_name):
        return exe_name.lower() in self._names

    def reg(self, key, name):
        if key.endswith(r"Blizzard Entertainment\Diablo II"):
            return {"InstallPath": INSTALL + "\\", "Save Path": SAVE + "\\"}.get(name)
        return None


def files():
    return {SAVE + r"\Testheld.d2s": d2s(), SAVE + r"\Testheld.key": b"tasten", SAVE + r"\Testheld.ma0": b"karte",
            SAVE + r"\Zweiter.d2s": d2s(name=b"Zweiter", cls=4)}


class D2sFormatTest(unittest.TestCase):
    def test_parse_fields_and_roundtrip(self):
        raw = d2s()
        f = D2s("x.d2s", raw)
        self.assertEqual(f.serialize(), raw)
        self.assertEqual(f.problems(), [])
        v = {x.key: x.value for x in f.fields()}
        self.assertEqual((v["name"], v["class"], v["headlevel"], v["level"]), ("Testheld", 1, 5, 5))
        self.assertEqual((v["strength"], v["gold"], v["hitpoints"], v["experience"]), (15, 1234, 40, 900))
        self.assertEqual(v["status"], "Expansion")
        self.assertEqual(v["difficulty"], "Normal Akt 1 (aktiv)")

    def test_set_keeps_length_and_fixes_checksum(self):
        raw = d2s()
        f = D2s("x.d2s", raw)
        f.set("strength", "200")
        f.set("gold", "50000")
        out = f.serialize()
        self.assertEqual(len(out), len(raw))
        g = D2s("x.d2s", out)
        self.assertEqual(g.problems(), [])
        v = {x.key: x.value for x in g.fields()}
        self.assertEqual((v["strength"], v["gold"], v["energy"]), (200, 50000, 35))
        self.assertEqual(out, d2s(stats=[(0, 200), (1, 35), (2, 25), (3, 10), (6, 40 * 256), (12, 5), (13, 900), (14, 50000)]))

    def test_set_limits(self):
        f = D2s("x.d2s", d2s())
        for key, val in (("gold", "50001"), ("strength", "1024"), ("level", "99"), ("experience", "1"),
                         ("statpts", "5"), ("strength", "viel")):
            with self.assertRaises(SaveError, msg=key):
                f.set(key, val)

    def test_other_version_is_readonly(self):
        f = D2s("x.d2s", d2s(version=0x5C))
        self.assertIsNone(f.stats)
        self.assertFalse(any(x.writable for x in f.fields()))
        with self.assertRaises(SaveError):
            f.set("strength", "20")

    def test_unknown_stat_is_readonly(self):
        WIDTH[16] = 3
        try:
            raw = bytearray(d2s(stats=[(0, 15), (16, 1)]))
        finally:
            del WIDTH[16]
        f = D2s("x.d2s", bytes(raw))
        self.assertIsNone(f.stats)
        self.assertIn("16", f.stats_error)

    def test_repair_and_truncated(self):
        b = bytearray(d2s())
        b[700] ^= 1
        f = D2s("x.d2s", bytes(b))
        self.assertEqual(len(f.problems()), 1)
        self.assertTrue(f.repair())
        self.assertEqual(f.problems(), [])
        cut = D2s("x.d2s", d2s()[:-2])
        self.assertTrue(any("Dateigröße" in p for p in cut.problems()))

    def test_not_a_d2s(self):
        with self.assertRaises(SaveError):
            D2s("x.d2s", bytes(800))


class NewSaveRemote(D2Remote):
    def reg(self, key, name):
        if name == "NewSavePath":
            return "\\Diablo II\\Save\\"
        return super().reg(key, name)


class D2GameTest(unittest.TestCase):
    def test_resolve_save_path(self):
        self.assertEqual(resolve_save_path("\\Diablo II\\Save\\", r"E:\Games\D2"), r"E:\Diablo II\Save")
        self.assertEqual(resolve_save_path(r"D:\Saves\D2\\", r"C:\Games\D2"), r"D:\Saves\D2")
        self.assertEqual(resolve_save_path("save", r"C:\Games\D2"), r"C:\Games\D2\save")

    def test_114d_uses_newsavepath_not_old_folder(self):
        fs = files()
        fs[r"C:\Diablo II\Save\Neu.d2s"] = d2s(name=b"Neu")
        fs[r"C:\Diablo II\Save\Europe\Neu.key"] = b"battle.net"
        g = resolve(NewSaveRemote(fs), DiabloII)
        self.assertEqual(g.save_dir(), r"C:\Diablo II\Save")
        self.assertEqual(g.profiles(), ["Neu"])                 # alter Ordner und Europe bleiben außen vor
        self.assertEqual([f.name for f in g.load("Neu").files], ["Neu.d2s"])

    def test_resolve_profiles_load(self):
        g = resolve(D2Remote(files()), find_game("d2"))
        self.assertIsInstance(g, DiabloII_114d)
        self.assertEqual(g.profiles(), ["Testheld", "Zweiter"])
        p = g.load("testheld")
        self.assertEqual(p.name, "Testheld")
        self.assertEqual([f.name for f in p.files], ["Testheld.d2s", "Testheld.key", "Testheld.ma0"])
        self.assertEqual(p.find("level")[1].value, 5)
        self.assertEqual(p.find("headlevel")[1].value, 5)

    def test_unknown_exe_readonly(self):
        g = resolve(D2Remote(files(), md5="1" * 32), DiabloII)
        self.assertIs(type(g), DiabloII)
        p = g.load("Testheld")
        p.set("gold", "10")
        with self.assertRaises(SaveError):
            g.store(p, r"C:\xpctl\backup\saves")

    def test_store_blocked_by_launcher(self):
        r = D2Remote(files(), running_names=["Diablo II.exe"])
        g = resolve(r, DiabloII)
        p = g.load("Testheld")
        p.set("gold", "10")
        with self.assertRaises(SaveError):
            g.store(p, r"C:\xpctl\backup\saves")
        self.assertEqual(r.writes, [])
        g.store(p, r"C:\xpctl\backup\saves", ignore_running=True)     # --force
        self.assertIn(SAVE + r"\Testheld.d2s", r.writes)

    def test_store_writes_and_backs_up(self):
        r = D2Remote(files())
        g = resolve(r, DiabloII)
        p = g.load("Testheld")
        p.set("gold", "4321")
        bdir, written = g.store(p, r"C:\xpctl\backup\saves")
        self.assertEqual(written, [SAVE + r"\Testheld.d2s"])
        self.assertEqual(r.read(bdir + r"\Testheld.d2s"), d2s())
        self.assertEqual(D2s("x", r.read(SAVE + r"\Testheld.d2s")).problems(), [])


if __name__ == "__main__":
    unittest.main()
