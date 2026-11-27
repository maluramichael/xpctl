"""Tests für savegames.archive: Spielstände als Zip mit Zeitstempel im Namen."""
from __future__ import annotations

import datetime as dt
import hashlib
import json
import stat
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from savegames import SaveError, resolve  # noqa: E402
from savegames.archive import archive_name, archives, snapshot  # noqa: E402
from savegames.diablo2 import DiabloII  # noqa: E402
from savegames.nfsu import NfsUnderground  # noqa: E402
from test_diablo2 import SAVE as D2SAVE, D2Remote, d2s  # noqa: E402
from test_savegames import SAVES, FakeRemote, nfsu_files  # noqa: E402

T1 = dt.datetime(2026, 11, 27, 20, 36, 11)
T2 = dt.datetime(2026, 11, 28, 9, 5, 0)


class ArchiveTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dest = Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def test_name_sorts_chronologically(self):
        self.assertEqual(archive_name("d2", T1), "d2_2026-11-27_20-36-11.zip")
        names = [archive_name("d2", T1 + dt.timedelta(hours=h)) for h in (0, 3, 30, 400)]
        self.assertEqual(sorted(names), names)

    def test_zip_holds_all_files_and_info(self):
        files = nfsu_files()
        files[SAVES + r"\alt\Spieler.cfg"] = b"unterordner"
        r = FakeRemote(files)
        s = snapshot(resolve(r, NfsUnderground), self.dest, now=T1)
        self.assertEqual(s.status, "neu")
        self.assertEqual(s.path, self.dest / "nfsu" / "nfsu_2026-11-27_20-36-11.zip")
        with zipfile.ZipFile(s.path) as z:
            self.assertIsNone(z.testzip())
            names = set(z.namelist())
            self.assertIn("alt/Spieler.cfg", names)
            self.assertIn("Spieler0.pro", names)
            self.assertEqual(z.read("Profiles.usr"), files[SAVES + r"\Profiles.usr"])
            self.assertEqual(z.getinfo("Gast.cfg").date_time, (2026, 11, 23, 17, 20, 0))
            info = json.loads(z.read("info.json"))
        self.assertEqual(len(names), len(files) + 1)
        self.assertEqual(info["game"], "nfsu")
        self.assertEqual(info["save_dir"], SAVES)
        self.assertTrue(info["created"].startswith("2026-11-27T20:36:11"))
        self.assertRegex(info["created"], r"[+-]\d\d:\d\d$")
        by = {f["path"]: f for f in info["files"]}
        self.assertEqual(by["Gast.cfg"]["md5"], hashlib.md5(files[SAVES + r"\Gast.cfg"]).hexdigest())
        self.assertEqual(stat.S_IMODE(s.path.stat().st_mode), 0o600)
        self.assertEqual(list(s.path.parent.glob("*.part")), [])

    def test_unchanged_makes_no_new_zip(self):
        r = FakeRemote(nfsu_files())
        g = resolve(r, NfsUnderground)
        first = snapshot(g, self.dest, now=T1)
        again = snapshot(g, self.dest, now=T2)
        self.assertEqual(again.status, "unverändert")
        self.assertEqual(again.previous, first.path)
        self.assertEqual(archives(self.dest, "nfsu"), [first.path])

    def test_change_makes_new_zip_old_stays(self):
        r = FakeRemote(nfsu_files())
        g = resolve(r, NfsUnderground)
        first = snapshot(g, self.dest, now=T1)
        before = first.path.read_bytes()
        r.write(SAVES + r"\Gast.cfg", b"neu")
        second = snapshot(g, self.dest, now=T2)
        self.assertEqual(second.status, "neu")
        self.assertEqual(second.previous, first.path)
        self.assertEqual(archives(self.dest, "nfsu"), [first.path, second.path])
        self.assertEqual(first.path.read_bytes(), before)

    def test_same_second_other_content_refused(self):
        r = FakeRemote(nfsu_files())
        g = resolve(r, NfsUnderground)
        first = snapshot(g, self.dest, now=T1)
        before = first.path.read_bytes()
        r.write(SAVES + r"\Gast.cfg", b"neu")
        with self.assertRaises(SaveError):
            snapshot(g, self.dest, now=T1)
        self.assertEqual(first.path.read_bytes(), before)

    def test_foreign_files_in_dest_ignored(self):
        r = FakeRemote(nfsu_files())
        g = resolve(r, NfsUnderground)
        (self.dest / "nfsu").mkdir()
        (self.dest / "nfsu" / "nfsu_2099-01-01_00-00-00.zip.part").write_bytes(b"kaputt")
        (self.dest / "nfsu" / "notiz.zip").write_bytes(b"x")
        s = snapshot(g, self.dest, now=T1)
        self.assertEqual(s.status, "neu")
        self.assertIsNone(s.previous)

    def test_broken_previous_zip_is_a_save_error(self):
        r = FakeRemote(nfsu_files())
        g = resolve(r, NfsUnderground)
        (self.dest / "nfsu").mkdir()
        (self.dest / "nfsu" / "nfsu_2026-01-01_00-00-00.zip").write_bytes(b"kein zip")
        with self.assertRaises(SaveError) as e:
            snapshot(g, self.dest, now=T1)
        self.assertIn("kein lesbares Zip", str(e.exception))

    def test_empty_folder_no_zip(self):
        s = snapshot(resolve(FakeRemote({}), NfsUnderground), self.dest, now=T1)
        self.assertEqual(s.status, "leer")
        self.assertFalse((self.dest / "nfsu").exists())

    def test_running_game_is_noted(self):
        r = FakeRemote(nfsu_files(), running=True)
        s = snapshot(resolve(r, NfsUnderground), self.dest, now=T1)
        self.assertEqual(s.running, ["Speed.exe"])
        with zipfile.ZipFile(s.path) as z:
            self.assertEqual(json.loads(z.read("info.json"))["running"], ["Speed.exe"])

    def test_read_only_version_is_backed_up(self):
        r = D2Remote({D2SAVE + r"\Held.d2s": d2s(b"Held")}, md5="0" * 32)
        g = resolve(r, DiabloII)
        self.assertFalse(g.writable)
        s = snapshot(g, self.dest, now=T1)
        self.assertEqual(s.status, "neu")
        self.assertEqual(s.files, ["Held.d2s"])


if __name__ == "__main__":
    unittest.main()
