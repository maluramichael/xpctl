"""Tests für client/savegames mit erzeugten Dateien (keine echten Spielstände im Repo).

    uv run --no-project python -m unittest discover -s client/tests

Echte Dateien zusätzlich prüfen (Round-Trip parse -> serialize muss Byte für Byte gleich sein):

    XPCTL_SAVES_DIR=/pfad/zu/kopien uv run --no-project python -m unittest discover -s client/tests
"""
from __future__ import annotations

import os
import struct
import sys
import unittest
import zlib
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from savegames import Entry, Remote, SaveError, find_game, games, resolve, versions  # noqa: E402
from savegames.nfsu import (NfsuCfg, NfsuOpt, NfsuProfiles, NfsuUgd,  # noqa: E402
                            NfsUnderground, NfsUnderground_20040115)

APPDATA = r"C:\Documents and Settings\All Users\Application Data"
SAVES = APPDATA + r"\NFS Underground"
INSTALL = r"C:\Games\NFSUND~1"
GOOD_MD5 = "95a0cba2466306469a33171e34995d05"


def adler_file(size: int, start: int, name_at: int, name: bytes, fill: int = 7) -> bytes:
    b = bytearray(bytes((i * fill) & 0xFF for i in range(size)))
    b[name_at:name_at + len(name) + 1] = name + b"\0"
    struct.pack_into("<I", b, size - 4, zlib.adler32(bytes(b[start:-4])) & 0xFFFFFFFF)
    return bytes(b)


def cfg(**over) -> bytes:
    vals = {"BPP": 32, "RES": 4, "ENVMAP": 2, "MOTIONBLUR": 0, "WorldLOD": 3}
    vals.update(over)
    lines = [f"{k}: {v}" for k, v in vals.items()] + ["", "", "123-45-0: 0 1 1 0 -1"]
    return "\r\n".join(lines).encode("ascii")


def usr(*names: str) -> bytes:
    out = struct.pack("<I", len(names))
    for n in names:
        rec = n.encode() + b"\0" + b"\x48\xf9\x12\x00junk"
        out += rec.ljust(32, b"\0")[:32]
    return out


class FakeRemote(Remote):
    def __init__(self, files: dict[str, bytes], md5: str | None = GOOD_MD5, running: bool = False):
        self.fs = {k.lower(): (k, v) for k, v in files.items()}
        self._md5 = md5
        self._running = running
        self.writes: list[str] = []

    def read(self, path):
        return self.fs[path.lower()][1]

    def write(self, path, data, mkdir=False):
        self.fs[path.lower()] = (path, bytes(data))
        self.writes.append(path)

    def files(self, folder):
        pre = folder.lower().rstrip("\\") + "\\"
        return [Entry(real.rsplit("\\", 1)[1], len(v), "")
                for k, (real, v) in self.fs.items() if k.startswith(pre) and "\\" not in k[len(pre):]]

    def md5(self, path):
        return self._md5 if path.lower().endswith("speed.exe") else None

    def running(self, exe_name):
        return self._running

    def reg(self, key, name):
        if key.endswith("NFS Underground") and "EA GAMES" in key and name == "Install Dir":
            return INSTALL + "\\"
        if key.endswith("Shell Folders") and name == "Common AppData":
            return APPDATA
        return None


def nfsu_files() -> dict[str, bytes]:
    return {
        SAVES + r"\Profiles.usr": usr("Spieler", "Gast"),
        SAVES + r"\Spieler.cfg": cfg(RES=3, MOTIONBLUR=1),
        SAVES + r"\Spieler.opt": adler_file(749, 4, 8, b"Spieler"),
        SAVES + r"\Spieler0.ugd": adler_file(63123, 47, 4, b"Spieler", fill=3),
        SAVES + r"\Spieler_autosave.ugd": adler_file(63123, 47, 4, b"auto", fill=5),
        SAVES + r"\Spieler0.pro": b"\x01\0\0\0\0\0\0\0geheim123\0" + b"\0" * 20,
        SAVES + r"\Gast.cfg": cfg(),
        SAVES + r"\Gast.opt": adler_file(749, 4, 8, b"Gast"),
    }


class RegistryTest(unittest.TestCase):
    def test_family_and_version(self):
        self.assertIn(NfsUnderground, games())
        self.assertEqual(versions(NfsUnderground), [NfsUnderground_20040115])
        self.assertIs(find_game("NFSU"), NfsUnderground)
        self.assertIs(NfsUnderground_20040115.family(), NfsUnderground)

    def test_unknown_game(self):
        with self.assertRaises(SaveError):
            find_game("gibtsnicht")


class FormatTest(unittest.TestCase):
    def test_cfg_roundtrip_and_set(self):
        raw = cfg(RES=3)
        f = NfsuCfg("x.cfg", raw)
        self.assertEqual(f.serialize(), raw)
        res = next(x for x in f.fields() if x.key == "RES")
        self.assertEqual(res.value, 3)
        f.set("res", "4")
        self.assertIn(b"RES: 4\r\n", f.serialize())
        self.assertTrue(f.serialize().endswith(b"123-45-0: 0 1 1 0 -1"))
        self.assertEqual(len(f.serialize()), len(raw))
        with self.assertRaises(SaveError):
            f.set("RES", "hoch")
        with self.assertRaises(SaveError):
            f.set("GIBTSNICHT", "1")

    def test_cfg_res4_label(self):
        f = NfsuCfg("x.cfg", cfg(RES=4))
        self.assertEqual(next(x for x in f.fields() if x.key == "RES").shown(), "4 (1280x1024)")

    def test_adler_ok_broken_repair(self):
        for cls, size, start, at in ((NfsuOpt, 749, 4, 8), (NfsuUgd, 63123, 47, 4)):
            raw = adler_file(size, start, at, b"Spieler")
            f = cls("x", raw)
            self.assertEqual(f.problems(), [])
            self.assertEqual(f.head_name(), "Spieler")
            self.assertEqual(f.serialize(), raw)
            # Byte vor dem Prüfbereich ändern: Prüfsumme bleibt gültig
            b = bytearray(raw)
            b[start - 1] ^= 0xFF
            self.assertEqual(cls("x", bytes(b)).problems(), [])
            # Byte im Prüfbereich ändern: Prüfsumme falsch, repair stellt sie her
            b = bytearray(raw)
            b[start + 10] ^= 0xFF
            g = cls("x", bytes(b))
            self.assertEqual(len(g.problems()), 1)
            self.assertTrue(g.repair())
            self.assertEqual(g.problems(), [])
            self.assertFalse(g.repair())

    def test_profiles_usr(self):
        f = NfsuProfiles("Profiles.usr", usr("Spieler", "Gast"))
        self.assertEqual(f.names, ["Spieler", "Gast"])
        self.assertEqual(f.problems(), [])
        self.assertEqual(NfsuProfiles("p", usr("a")[:-3]).problems()[0][:5], "Größe")


class GameTest(unittest.TestCase):
    def test_resolve_known_exe(self):
        g = resolve(FakeRemote(nfsu_files()), NfsUnderground)
        self.assertIsInstance(g, NfsUnderground_20040115)
        self.assertTrue(g.writable)
        self.assertEqual(g.profiles(), ["Spieler", "Gast"])

    def test_resolve_unknown_exe_is_readonly(self):
        g = resolve(FakeRemote(nfsu_files(), md5="0" * 32), NfsUnderground)
        self.assertIs(type(g), NfsUnderground)
        self.assertFalse(g.writable)
        p = g.load("Spieler")
        p.set("RES", "4")
        with self.assertRaises(SaveError):
            g.store(p, r"C:\xpctl\backup\saves")

    def test_load_profile_and_hide_online(self):
        g = resolve(FakeRemote(nfsu_files()), NfsUnderground)
        p = g.load("spieler")
        self.assertEqual(p.name, "Spieler")
        kinds = [f.kind for f in p.files]
        self.assertEqual(kinds, ["cfg", "opt", "ugd", "autosave", "online"])
        shown = " ".join(str(x.value) for _, x in p.fields())
        self.assertNotIn("geheim", shown)
        self.assertEqual(p.problems(), [])

    def test_ambiguous_and_unknown_field(self):
        p = resolve(FakeRemote(nfsu_files()), NfsUnderground).load("Spieler")
        with self.assertRaises(SaveError):
            p.find("checksum")            # in opt, ugd und autosave
        self.assertEqual(p.find("opt.checksum")[0].kind, "opt")
        with self.assertRaises(SaveError):
            p.set("opt.checksum", "1")    # nicht schreibbar
        with self.assertRaises(SaveError):
            p.find("gibtsnicht")

    def test_store_backup_then_write_and_verify(self):
        r = FakeRemote(nfsu_files())
        g = resolve(r, NfsUnderground)
        p = g.load("Spieler")
        before = p.file("cfg").original
        p.set("MOTIONBLUR", "0")
        bdir, written = g.store(p, r"C:\xpctl\backup\saves")
        self.assertEqual(written, [SAVES + r"\Spieler.cfg"])
        self.assertTrue(bdir.startswith(r"C:\xpctl\backup\saves\nfsu" + "\\"))
        self.assertEqual(r.read(bdir + r"\Spieler.cfg"), before)
        self.assertIn(b"MOTIONBLUR: 0", r.read(SAVES + r"\Spieler.cfg"))
        self.assertEqual(r.writes[0], bdir + r"\Spieler.cfg")     # Sicherung zuerst
        self.assertEqual(g.store(p, r"C:\xpctl\backup\saves"), ("", []))  # nichts mehr zu tun

    def test_backup_never_overwritten(self):
        r = FakeRemote(nfsu_files())
        g = resolve(r, NfsUnderground)
        p = g.load("Spieler")
        p.set("MOTIONBLUR", "0")
        b1, _ = g.store(p, r"C:\xpctl\backup\saves")
        p.set("MOTIONBLUR", "1")
        b2, _ = g.store(p, r"C:\xpctl\backup\saves")   # gleiche Sekunde
        self.assertNotEqual(b1, b2)
        self.assertIn(b"MOTIONBLUR: 1", r.read(b1 + r"\Spieler.cfg"))   # erste Sicherung = Ursprung
        self.assertIn(b"MOTIONBLUR: 0", r.read(b2 + r"\Spieler.cfg"))

    def test_backup_all_files_without_profile(self):
        files = nfsu_files()
        files[SAVES + r"\Spieler.cfg.alt"] = b"irgendwas"
        r = FakeRemote(files)
        g = resolve(r, NfsUnderground)
        bdir, done = g.backup(None, r"C:\xpctl\backup\saves")
        self.assertEqual(len(done), len(files))
        self.assertIn("Spieler.cfg.alt", done)
        self.assertIn("Spieler0.pro", done)     # Sicherung ist vollständig, angezeigt wird sie nicht
        self.assertEqual(r.read(bdir + r"\Profiles.usr"), files[SAVES + r"\Profiles.usr"])

    def test_backup_one_profile_includes_profile_list(self):
        r = FakeRemote(nfsu_files())
        g = resolve(r, NfsUnderground)
        _, done = g.backup(["Gast"], r"C:\xpctl\backup\saves")
        self.assertEqual(sorted(done), ["Gast.cfg", "Gast.opt", "Profiles.usr"])

    def test_store_refuses_while_running(self):
        r = FakeRemote(nfsu_files(), running=True)
        g = resolve(r, NfsUnderground)
        p = g.load("Spieler")
        p.set("RES", "4")
        with self.assertRaises(SaveError):
            g.store(p, r"C:\xpctl\backup\saves")
        self.assertEqual(r.writes, [])

    def test_not_installed(self):
        class Empty(FakeRemote):
            def reg(self, key, name):
                return None
        self.assertIsNone(resolve(Empty({}), NfsUnderground))


@unittest.skipUnless(os.environ.get("XPCTL_SAVES_DIR"), "XPCTL_SAVES_DIR nicht gesetzt")
class RealFilesTest(unittest.TestCase):
    """Round-Trip und Prüfsummen an echten Kopien (bleiben außerhalb des Repos)."""

    def test_roundtrip(self):
        d = Path(os.environ["XPCTL_SAVES_DIR"])
        seen = 0
        for p in sorted(d.iterdir()):
            low = p.name.lower()
            cls = (NfsuProfiles if low == "profiles.usr" else NfsuCfg if low.endswith(".cfg")
                   else NfsuOpt if low.endswith(".opt") else NfsuUgd if low.endswith(".ugd") else None)
            if cls is None:
                continue
            raw = p.read_bytes()
            f = cls(str(p), raw)
            self.assertEqual(f.serialize(), raw, p.name)
            self.assertEqual(f.problems(), [], p.name)
            seen += 1
        self.assertGreater(seen, 0)
        print(f"\n  {seen} echte Dateien: Round-Trip und Prüfsummen ok", file=sys.stderr)


if __name__ == "__main__":
    unittest.main()
