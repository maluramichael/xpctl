# Protocol between xpctl and xpserver

TCP, default port 5050. Every message is a frame: 4 bytes length (big endian, at most 16 MB), followed by the payload. Control messages are UTF-8 JSON objects, file contents travel as raw frames.

## Login

1. Server: `{"hello": "xpserver", "version": 17, "nonce": "<32 hex chars>"}`
2. Client: `{"auth": HMAC(key, "client|" + nonce), "cnonce": "<32 hex chars>"}`
3. Server: `{"ok": true, "auth": HMAC(key, "server|" + cnonce)}` or, after one second, `{"ok": false, "error": "auth"}`

HMAC is HMAC-SHA256 as a hex string, `key` is the bytes of the password. The client checks the server's answer, so both sides prove who they are without sending the password. The server closes connections from addresses outside `allow` right away.

## Requests

After that the client sends any number of requests `{"op": "<name>", ...}` and gets one answer each, `{"ok": true, ...}` or `{"ok": false, "error": "<text>"}`. `{"op": "bye"}` ends the connection. The server answers unknown ops with `unbekannte op: <name>` (unknown op).

The fields of each op are defined in the client (`client/xpctl.py`, functions `cmd_<op>`) and in the server (`server/Ops/*.cs`, attribute `[Op("<op>")]`).

## Data streams

Ops that carry file contents run in three steps:

- **put**: client `{"op": "put", "path", "size", "name", "mkdir"}`, server `{"ok": true, "ready": true, "target"}`, client sends `size` bytes in frames (256 KB), server `{"ok": true, "size", "sha256", "target"}`. The data goes to `<target>.part` first and is renamed at the end.
- **get**: client `{"op": "get", "path"}`, server `{"ok": true, "ready": true, "size"}`, then the frames, then `{"ok": true, "size", "sha256"}`.
- **shot**: like get, the payload is zlib-compressed BGR rows (24 bit, `stride` from the first answer, top row first). The client turns it into a PNG.
- **update**: client `{"op": "update", "size", "sha256"}`, server `{"ok": true, "ready": true}`, client sends the new `xpserver.exe`, server `{"ok": true, "restarting": true}` and hands over to the new version.

## ping

`{"op": "ping"}` returns `version`, `sha256` (of the running exe), `kind` (`"net"` for the .NET server, missing on the old Python server), `runtime`, `host`, `os`, `script`, `cwd`, `time`, `supervised`.
