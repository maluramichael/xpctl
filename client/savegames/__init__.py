"""Spielstände und Profile lesen und schreiben, pro Spiel und Exe-Version eine Klasse.

Neues Spiel: Modul in diesem Ordner anlegen, eine Basisklasse von ``Game`` mit ``id`` und pro
geprüfter Exe eine Unterklasse mit ``exe_md5`` und Versionsnamen im Klassennamen, dann das Modul
hier importieren. Details in client/README.md, Abschnitt "Spielstände".
"""
from .core import (Entry, Field, Game, OpaqueFile, Profile, Remote, SaveError, SaveFile,  # noqa: F401
                   adler32, find_game, games, join, resolve, versions)
from . import nfsu  # noqa: F401  (registriert die Klassen)
