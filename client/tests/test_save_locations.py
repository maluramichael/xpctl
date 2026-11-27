"""client/save-locations.json (Sicherung auf dem XP-Rechner) muss zu client/savegames passen.

Ein kleiner Nachbau der Auflösung von xpserver (Ops/SaveBackup.cs) läuft gegen dieselben Fake-Rechner
wie die Formattests und muss denselben Spielstand-Ordner finden wie ``Game.save_dir()``.
"""
from __future__ import annotations

import json
import sys
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE))

from savegames import games, resolve  # noqa: E402
from savegames.diablo2 import DiabloII  # noqa: E402
from savegames.nfsu import SHELL_FOLDERS, NfsUnderground  # noqa: E402
from test_diablo2 import D2Remote, NewSaveRemote, d2s, files as d2_files  # noqa: E402
from test_savegames import FakeRemote, nfsu_files  # noqa: E402

DB = json.loads((HERE.parent / "save-locations.json").read_text(encoding="utf-8"))


def _exists(remote, folder: str) -> bool:
    pre = folder.lower().rstrip("\\") + "\\"
    return any(k.startswith(pre) for k in remote.fs)


def _reg(remote, spec: str):
    key, name = spec.rsplit("|", 1)
    return remote.reg(key, name)


def _from_reg_value(v: str, install: str) -> str:
    v = v.strip().rstrip("\\")
    if len(v) >= 2 and v[1] == ":":
        return v
    if v.startswith("\\"):
        return install[:2] + v
    return install + "\\" + v


def locate(remote, entry):
    """(Installationsordner, Spielstand-Ordner) wie xpserver; None, wenn nicht installiert."""
    install = next((v.rstrip("\\") for v in (_reg(remote, s) for s in entry["install"]) if v), None)
    if install is None:
        return None
    folders = {"{install}": install, "{common_appdata}": remote.reg(SHELL_FOLDERS, "Common AppData") or ""}
    for cand in entry["saves"]:
        if cand.startswith("reg:"):
            v = _reg(remote, cand[4:])
            if not v:
                continue
            path = _from_reg_value(v, install)
        else:
            path = cand
            for k, val in folders.items():
                path = path.replace(k, val)
        if _exists(remote, path):
            return install, path
    return install, None


class SaveLocationsTest(unittest.TestCase):
    def entry(self, gid):
        return next(e for e in DB["games"] if e["id"] == gid)

    def test_every_python_game_has_an_entry(self):
        ids = [e["id"] for e in DB["games"]]
        self.assertEqual(len(ids), len(set(ids)))
        for fam in games():
            self.assertIn(fam.id, ids)
            e = self.entry(fam.id)
            self.assertEqual(e["title"], fam.title)
            self.assertEqual(e["exe"].lower(), fam.exe.lower())
            self.assertEqual({p.lower() for p in e["processes"]},
                             {p.lower() for p in (fam.processes or (fam.exe,))})

    def test_nfsu_same_folder(self):
        r = FakeRemote(nfsu_files())
        self.assertEqual(locate(r, self.entry("nfsu"))[1], resolve(r, NfsUnderground).save_dir())

    def test_d2_old_save_path(self):
        r = D2Remote(d2_files())
        self.assertEqual(locate(r, self.entry("d2"))[1], resolve(r, DiabloII).save_dir())

    def test_d2_114d_newsavepath_before_old_folder(self):
        fs = d2_files()                                    # alter Save Path hat auch Dateien
        fs[r"C:\Diablo II\Save\Neu.d2s"] = d2s(name=b"Neu")
        r = NewSaveRemote(fs)
        self.assertEqual(locate(r, self.entry("d2"))[1], r"C:\Diablo II\Save")
        self.assertEqual(locate(r, self.entry("d2"))[1], resolve(r, DiabloII).save_dir())

    def test_not_installed(self):
        class Empty(FakeRemote):
            def reg(self, key, name):
                return None
        self.assertIsNone(locate(Empty({}), self.entry("nfsu")))


if __name__ == "__main__":
    unittest.main()
