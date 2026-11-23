"""Prüft client/game-versions.json: Felder, eindeutige Zuordnung, Abgleich mit den Spielstand-Klassen."""
from __future__ import annotations

import collections
import json
import re
import sys
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))

from savegames import games, versions  # noqa: E402

DB = json.loads((HERE.parent / "game-versions.json").read_text(encoding="utf-8"))
GAMES = DB["games"]


class GameDbTest(unittest.TestCase):
    def test_header(self):
        self.assertRegex(DB["stand"], r"^\d{4}-\d{2}-\d{2}$")

    def test_fields(self):
        for e in GAMES:
            with self.subTest(game=e["game"], exe=e["exe"]):
                self.assertRegex(e["md5"], r"^[0-9a-f]{32}$")
                self.assertIsInstance(e["size"], int)
                self.assertIn(e["status"], ("aktuell", "patch", "unklar"))
                self.assertTrue(e["version"] and e["latest"])
                for r in e.get("reg", []):
                    self.assertRegex(r, r"^HK(LM|CU)\\.+\|.+$")
                if re.search(r"unklar|vermut|wahrscheinlich", e["version"], re.I):
                    self.assertEqual(e["status"], "unklar", "unsichere Version darf nicht aktuell heißen")

    def test_exe_and_size_unique(self):
        """Erste Anzeige ohne Hash: Exe-Name + Größe muss genau einen md5 ergeben."""
        seen = collections.defaultdict(set)
        for e in GAMES:
            seen[(e["exe"].lower(), e["size"])].add(e["md5"])
        self.assertEqual({k: v for k, v in seen.items() if len(v) > 1}, {})

    def test_same_md5_same_verdict(self):
        """Mehrere Spiele mit derselben Exe (DOSBox, Launcher) dürfen sich beim Status nicht widersprechen,
        außer die Einträge unterscheiden sich per probe oder gog_id."""
        by = collections.defaultdict(list)
        for e in GAMES:
            if not e.get("gog_id"):
                by[e["md5"]].append(e)
        for md5, es in by.items():
            self.assertEqual(len({e["status"] for e in es}), 1, [e["game"] for e in es])

    def test_savegame_versions_known(self):
        known = {e["md5"] for e in GAMES}
        for cls in (v for fam in games() for v in versions(fam)):
            for m in cls.exe_md5:
                self.assertIn(m, known, f"{cls.__name__}: Exe fehlt in game-versions.json")


if __name__ == "__main__":
    unittest.main()
