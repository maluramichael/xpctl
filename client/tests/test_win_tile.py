"""Prüft tile_rects (win tile): Gitter über dem Bildschirm, Spalten, Abstand."""
from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import xpctl  # noqa: E402


class TileRects(unittest.TestCase):
    def test_tile_3x3_fills_screen_above_taskbar(self):
        r = xpctl.tile_rects(9, 0, 1920, 1080, bottom=30)
        self.assertEqual(len(r), 9)
        self.assertEqual(r[0], (0, 0, 640, 350))
        self.assertEqual(r[8], (1280, 700, 640, 350))
        self.assertLessEqual(max(y + h for _, y, _, h in r), 1050)

    def test_tile_cols_and_gap(self):
        r = xpctl.tile_rects(5, 2, 1000, 600, gap=10)
        self.assertEqual(len(r), 5)
        self.assertEqual(r[1][0], 10 + r[0][2] + 10)
        self.assertTrue(r[4][1] > r[2][1] > r[0][1])

    def test_tile_empty(self):
        self.assertEqual(xpctl.tile_rects(0, 0, 800, 600), [])


if __name__ == "__main__":
    unittest.main()
