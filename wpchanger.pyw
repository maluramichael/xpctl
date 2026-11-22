# Wallpaper-Wechsler für den XP-Rechner (Windows XP, Python 3.4, pythonw = kein Fenster).
# Setzt alle INTERVALL Sekunden ein zufälliges BMP aus ORDNER als Desktop-Hintergrund.
# Jedes Bild kommt einmal dran, bevor sich etwas wiederholt. Neue Bilder im Ordner
# werden beim nächsten Durchlauf mitgenommen. Autostart über den Startup-Ordner.
import ctypes
import os
import random
import sys
import time
import winreg

ORDNER = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.expanduser("~"), "Wallpapers")  # Ordner mit BMP-Bildern als Argument
INTERVALL = 30 * 60
LOG = os.path.join(ORDNER, "_wechsler.log")

SPI_SETDESKWALLPAPER = 20
SPIF_UPDATEINIFILE_SENDCHANGE = 0x01 | 0x02


def log(text):
    try:
        with open(LOG, "a") as f:
            f.write(time.strftime("%Y-%m-%d %H:%M:%S ") + text + "\n")
    except OSError:
        pass


def nur_einmal():
    # Zweite Instanz (z. B. doppelt gestartet) beendet sich sofort.
    ctypes.windll.kernel32.CreateMutexW(None, False, "xpctl-wpchanger")
    if ctypes.windll.kernel32.GetLastError() == 183:  # ERROR_ALREADY_EXISTS
        sys.exit(0)


def stil_zentriert():
    # Bilder sind exakt 1280x1024: zentriert, nicht kacheln.
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Control Panel\Desktop", 0, winreg.KEY_SET_VALUE) as k:
        winreg.SetValueEx(k, "WallpaperStyle", 0, winreg.REG_SZ, "0")
        winreg.SetValueEx(k, "TileWallpaper", 0, winreg.REG_SZ, "0")


def bilder():
    try:
        return [os.path.join(ORDNER, n) for n in os.listdir(ORDNER) if n.lower().endswith(".bmp")]
    except OSError:
        return []


def setzen(pfad):
    ok = ctypes.windll.user32.SystemParametersInfoW(
        SPI_SETDESKWALLPAPER, 0, pfad, SPIF_UPDATEINIFILE_SENDCHANGE)
    if not ok:
        log("Fehler beim Setzen: " + pfad)


def main():
    nur_einmal()
    try:
        stil_zentriert()
    except OSError as e:
        log("Stil nicht gesetzt: %s" % e)
    stapel = []
    while True:
        if not stapel:
            stapel = bilder()
            random.shuffle(stapel)
        if stapel:
            setzen(stapel.pop())
        time.sleep(INTERVALL)


if __name__ == "__main__":
    try:
        main()
    except Exception as e:  # pythonw verschluckt sonst alles
        log("Abbruch: %r" % e)
