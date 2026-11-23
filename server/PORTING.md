# Regeln für Ops im .NET-Server

Der Port ist abgeschlossen (alle 46 Ops, 243 Vergleichsfälle gleich). Diese Datei bleibt als Regelwerk für neue Ops und Änderungen.

Referenz war `server/legacy-python/xpserver.py` (Python 3.4). Ziel: Der .NET-Server liefert für jede Anfrage
**dieselbe JSON-Antwort** wie der Python-Server (Feldnamen, Reihenfolge, Typen, Texte, Fehlermeldungen).
Der Client `client/xpctl.py` spricht mit beiden Servern; abgesehen von `update` und `migrate` behandelt er sie gleich. Neue oder geänderte Ops: Antwortformat so halten, dass ältere Clients weiter funktionieren.

## Gerüst (fertig, nicht ändern ohne Absprache)

- `Core/Net.cs`: Ops sind `static J Name(Conn c, Req r)` mit Attribut `[Op("name")]`, werden per Reflection gefunden.
  Jede Gruppe hat eine eigene Datei `Ops/<Gruppe>.cs` (eigene `static class`, eigene P/Invoke-Deklarationen darin).
- `J` = JSON-Objekt (Einfügereihenfolge wie Python-dict). `J.Ok("k", v, ...)`, `J.Err("text")`, `J.O(...)`.
  Listen: `List<object>` oder `List<J>`. Zahlen als `long` (Python int) bzw. `double` (Python float). Kein `int`-Mix nötig, aber erlaubt.
- `Req`: `r.Need("k")` = `req["k"]` (KeyError), `r.Str("k", def)` = `req.get("k", def)`, `r.Or("k", def)` = `req.get("k") or def`,
  `r.Int`, `r.Dbl`, `r.Bool` (= `bool(req.get(k))`), `r.List`, `r.Strs`, `r.Has`.
- Fehler: `throw new OpError("text")` wird zu `"ValueError: text"` (wie Python `raise ValueError`). Anderer Python-Typ:
  `new OpError("KeyError", "...")`. Unerwartete .NET-Ausnahmen werden grob auf Python-Namen gemappt (`Server.ErrText`).
- `Conn`: `c.SendJson(o)`, `c.SendFrame(bytes[, off, len])`, `c.RecvFrame()`, `Conn.Chunk` (256 KB), `Conn.MaxFrame`.
- `Core/Reg.cs`: winreg-Nachbau (`Reg.Read` = reg_read, `Reg.Get` = rget, `Reg.Subs` = rsubs, `Reg.Vals` = rvals,
  `Reg.Show` = show_data, `Reg.Hive` = hive_handle, `Reg.Split` = split_regpath, `Reg.Plain` = _plain, `Reg.Open` + `Key.Query/Values/SubKeys`).
  Typen wie winreg: SZ/EXPAND_SZ = string (nicht expandiert), MULTI_SZ = List<object>, DWORD/QWORD = long, binär = byte[].
- `Core/Proc.cs`: `Proc.Sh(cmd, timeout)` = sh(), `Proc.Run(Proc.Shell(cmd), timeout, mergeErr)` = Popen(shell=True)+communicate,
  `Proc.Oem` = oem_encoding(), `Proc.KillTree(pid)`, `Proc.Detach(cmd)`. Nie eigene `Process.Start` mit Konsolenfenster:
  immer `CreateNoWindow = true`, `UseShellExecute = false` (die App ist ein GUI-Prozess, sonst blitzen cmd-Fenster auf).
- `Core/Common.cs`: `Common.SysRoot()`, `FolderPath(csidl)`, `AllUsersDesktop`, `MyDesktop`, `LauncherName()`, `CdDrive(r)`,
  `VolLabel(drive)`, `NormDate`, `ProgramKind`, `CollectPrograms()`, `KbRe`, `UpdateNameRe`, `HklmUninstall`, `GetDriveTypeW`.
- `Core/Config.cs`: `Cfg.Get/Str/Strs/Need(key, what)` = CFG / cfg_need.
- `Py.Truthy`, `Py.Repr` (= str()), `Py.SortBy(list, key)` (stabile Sortierung wie sorted(key=...), Ordinal).

## .NET-3.5-Fallen

- Nicht vorhanden (erst 4.0): `string.Join(IEnumerable)` (nur `string[]`), `string.IsNullOrWhiteSpace`, `Enum.HasFlag`,
  `Stream.CopyTo`, `Tuple`, `Lazy`, `Task`, `ConcurrentDictionary`, `File.ReadLines`, `Directory.EnumerateFiles`,
  `RegistryKey.Handle`, `Environment.Is64BitOperatingSystem`. Der Compiler meldet das.
- `List.Sort` ist instabil: für Python-`sorted` immer `Py.SortBy` oder LINQ `OrderBy` nehmen.
- Python-`%` mit Zahlen, `round()` (Banker's Rounding in Py3 wie .NET `Math.Round` Standard), Gleitkomma-Ausgabe: `Json` schreibt doubles wie Python (`1.0`, `0.1`).
- Zeit: `time.strftime` lokal, `os.path.getmtime` = `File.GetLastWriteTime`. Dateinamen-Sortierung von `os.listdir` (NTFS: alphabetisch, wie `Directory.GetFileSystemEntries`).
- Python `str.lower()` = `ToLowerInvariant()`. Regex: Python `re` ≈ .NET Regex, aber `\d` in .NET matcht Unicode-Ziffern (egal), `$` matcht vor `\n` in beiden.
- COM (IShellLink, IPersistFile): Client-Threads sind STA. `[ComImport]`-Interfaces oder vtable-Aufrufe wie im Python-Code.
- `zlib.compress`: .NET 3.5 hat nur `DeflateStream` (roh). zlib = 2 Byte Header (0x78 0x9C) + Deflate + Adler-32 (Big Endian).
- Prozessname der App ist `xpserver.exe` (steht schon in MOUNT_SAFE_FG).

## Testen auf einem XP-Rechner

- Bauen: `cd server && dotnet build -c Release` -> `server/bin/Release/net35/xpserver.exe`.
- Jede Gruppe bekommt einen eigenen Testport (5051 bis 5059) und Ordner auf dem XP-Rechner, parallel zum Referenzserver auf 5050. Firewall-Port vorher freigeben. Einrichten:
  `client/xpctl.py put server/bin/Release/net35/xpserver.exe 'C:\xpctl-net\<gruppe>\' --mkdir`,
  `client/xpctl.py run 'copy /y C:\xpctl-net\core\xpserver.json C:\xpctl-net\<gruppe>\ & copy /y C:\xpctl\xpctl.key C:\xpctl-net\<gruppe>\'`,
  starten: `client/xpctl.py run --detach 'C:\xpctl-net\<gruppe>\xpserver.exe --port <P> --no-watchdog'`.
  Neue Version: eigenen Prozess per PID beenden (`client/xpctl.py ps xpserver --cmdline`, dann `client/xpctl.py kill <PID>`), Exe neu kopieren, neu starten.
- Vergleich: `server/tests/parity.py --b <P> server/tests/cases/<gruppe>.jsonl` (A = Python-Server 5050). Fälle als JSON-Zeilen,
  `_ignore` für Felder, die sich zwischen zwei Aufrufen ändern dürfen.
- `export XPCTL_HOST=<Adresse des XP-Rechners>`. Passwort wie beim Client (`~/.config/xpctl/key`).

## Verbote auf dem Testrechner

- Python-Server (Port 5050, python.exe, xpserver.bat-cmd.exe) und fremde xpserver.exe-Instanzen nie beenden oder neu starten. Nur den eigenen Prozess per PID.
- Nichts installieren/deinstallieren, keine Treiber, keine NVIDIA-Einstellungen setzen, kein CD-Laufwerk auswerfen, keine Images einhängen,
  keine Desktop-Verknüpfungen oder Icon-Positionen dauerhaft ändern, nichts außerhalb `C:\xpctl-net\<gruppe>\` schreiben oder löschen.
  Solche Ops portieren, aber nur mit harmlosen Fehlerfällen oder Testobjekten im eigenen Ordner prüfen; im Bericht als "portiert, nicht live geprüft" melden.
- Eingabe-Ops (click/type/key/focus/close/press/settext) nur gegen Fenster, die man selbst gestartet hat (z. B. notepad), und danach schließen.
- Keine Dateien außerhalb von `server/Ops/<Gruppe>.cs` und `server/tests/cases/<gruppe>.jsonl` ändern. Fehlt etwas im Gerüst: im Bericht melden, lokal in der eigenen Datei lösen.
