import xpctl


def test_tile_3x3_fills_screen_above_taskbar():
    r = xpctl.tile_rects(9, 0, 1920, 1080, bottom=30)
    assert len(r) == 9
    assert r[0] == (0, 0, 640, 350)
    assert r[8] == (1280, 700, 640, 350)
    assert max(y + h for _, y, _, h in r) <= 1050


def test_tile_cols_and_gap():
    r = xpctl.tile_rects(5, 2, 1000, 600, gap=10)
    assert len(r) == 5
    assert r[1][0] == 10 + r[0][2] + 10
    assert r[4][1] > r[2][1] > r[0][1]


def test_tile_empty():
    assert xpctl.tile_rects(0, 0, 800, 600) == []
