# Protokoll zwischen xpctl und xpserver

TCP, Standardport 5050. Jede Nachricht ist ein Frame: 4 Byte Länge (Big Endian, höchstens 16 MB), danach die Nutzdaten. Steuernachrichten sind UTF-8-JSON-Objekte, Dateiinhalte gehen als rohe Frames.

## Anmeldung

1. Server: `{"hello": "xpserver", "version": 16, "nonce": "<32 Hex-Zeichen>"}`
2. Client: `{"auth": HMAC(key, "client|" + nonce), "cnonce": "<32 Hex-Zeichen>"}`
3. Server: `{"ok": true, "auth": HMAC(key, "server|" + cnonce)}` oder nach einer Sekunde `{"ok": false, "error": "auth"}`

HMAC ist HMAC-SHA256 als Hex-String, `key` sind die Bytes des Passworts. Der Client prüft die Antwort des Servers, so weisen sich beide Seiten aus, ohne dass das Passwort übertragen wird. Verbindungen von Adressen außerhalb von `allow` schließt der Server sofort.

## Anfragen

Danach schickt der Client beliebig viele Anfragen `{"op": "<name>", ...}` und bekommt je eine Antwort `{"ok": true, ...}` oder `{"ok": false, "error": "<Text>"}`. `{"op": "bye"}` beendet die Verbindung. Unbekannte Ops beantwortet der Server mit `unbekannte op: <name>`.

Die Felder jeder Op stehen im Client (`client/xpctl.py`, Funktionen `cmd_<op>`) und im Server (`server/Ops/*.cs`, Attribut `[Op("<op>")]`).

## Datenströme

Ops mit Dateiinhalt laufen in drei Schritten:

- **put**: Client `{"op": "put", "path", "size", "name", "mkdir"}`, Server `{"ok": true, "ready": true, "target"}`, Client schickt `size` Bytes in Frames (256 KB), Server `{"ok": true, "size", "sha256", "target"}`. Geschrieben wird erst in `<ziel>.part`, dann umbenannt.
- **get**: Client `{"op": "get", "path"}`, Server `{"ok": true, "ready": true, "size"}`, dann die Frames, dann `{"ok": true, "size", "sha256"}`.
- **shot**: wie get, die Nutzdaten sind zlib-komprimierte BGR-Zeilen (24 Bit, `stride` aus der ersten Antwort, oben beginnend). Der Client macht daraus ein PNG.
- **update**: Client `{"op": "update", "size", "sha256"}`, Server `{"ok": true, "ready": true}`, Client schickt die neue `xpserver.exe`, Server `{"ok": true, "restarting": true}` und übergibt an die neue Version.

## ping

`{"op": "ping"}` liefert `version`, `sha256` (der laufenden Exe), `kind` (`"net"` beim .NET-Server, fehlt beim alten Python-Server), `runtime`, `host`, `os`, `script`, `cwd`, `time`, `supervised`.
