# Rules for ops in the .NET server

The port is finished (all 46 ops, 243 comparison cases identical). This file stays as the rulebook for new ops and changes.

The reference was `server/legacy-python/xpserver.py` (Python 3.4). Goal: for every request the .NET server returns
**the same JSON answer** as the Python server (field names, order, types, texts, error messages).
The client `client/xpctl.py` talks to both servers; apart from `update` and `migrate` it treats them the same. New or changed ops: keep the response format so that older clients keep working.

## Scaffolding (done, don't change without agreement)

- `Core/Net.cs`: ops are `static J Name(Conn c, Req r)` with the attribute `[Op("name")]`, found via reflection.
  Each group has its own file `Ops/<Group>.cs` (its own `static class`, its own P/Invoke declarations inside).
- `J` = JSON object (insertion order like a Python dict). `J.Ok("k", v, ...)`, `J.Err("text")`, `J.O(...)`.
  Lists: `List<object>` or `List<J>`. Numbers as `long` (Python int) or `double` (Python float). No need to mix in `int`, but allowed.
- `Req`: `r.Need("k")` = `req["k"]` (KeyError), `r.Str("k", def)` = `req.get("k", def)`, `r.Or("k", def)` = `req.get("k") or def`,
  `r.Int`, `r.Dbl`, `r.Bool` (= `bool(req.get(k))`), `r.List`, `r.Strs`, `r.Has`.
- Errors: `throw new OpError("text")` becomes `"ValueError: text"` (like Python `raise ValueError`). Other Python type:
  `new OpError("KeyError", "...")`. Unexpected .NET exceptions are roughly mapped to Python names (`Server.ErrText`).
- `Conn`: `c.SendJson(o)`, `c.SendFrame(bytes[, off, len])`, `c.RecvFrame()`, `Conn.Chunk` (256 KB), `Conn.MaxFrame`.
- `Core/Reg.cs`: winreg replica (`Reg.Read` = reg_read, `Reg.Get` = rget, `Reg.Subs` = rsubs, `Reg.Vals` = rvals,
  `Reg.Show` = show_data, `Reg.Hive` = hive_handle, `Reg.Split` = split_regpath, `Reg.Plain` = _plain, `Reg.Open` + `Key.Query/Values/SubKeys`).
  Types like winreg: SZ/EXPAND_SZ = string (not expanded), MULTI_SZ = List<object>, DWORD/QWORD = long, binary = byte[].
- `Core/Proc.cs`: `Proc.Sh(cmd, timeout)` = sh(), `Proc.Run(Proc.Shell(cmd), timeout, mergeErr)` = Popen(shell=True)+communicate,
  `Proc.Oem` = oem_encoding(), `Proc.KillTree(pid)`, `Proc.Detach(cmd)`. Never your own `Process.Start` with a console window:
  always `CreateNoWindow = true`, `UseShellExecute = false` (the app is a GUI process, otherwise cmd windows flash up).
- `Core/Common.cs`: `Common.SysRoot()`, `FolderPath(csidl)`, `AllUsersDesktop`, `MyDesktop`, `LauncherName()`, `CdDrive(r)`,
  `VolLabel(drive)`, `NormDate`, `ProgramKind`, `CollectPrograms()`, `KbRe`, `UpdateNameRe`, `HklmUninstall`, `GetDriveTypeW`.
- `Core/Config.cs`: `Cfg.Get/Str/Strs/Need(key, what)` = CFG / cfg_need.
- `Py.Truthy`, `Py.Repr` (= str()), `Py.SortBy(list, key)` (stable sort like sorted(key=...), ordinal).

## .NET 3.5 traps

- Not available (only from 4.0): `string.Join(IEnumerable)` (only `string[]`), `string.IsNullOrWhiteSpace`, `Enum.HasFlag`,
  `Stream.CopyTo`, `Tuple`, `Lazy`, `Task`, `ConcurrentDictionary`, `File.ReadLines`, `Directory.EnumerateFiles`,
  `RegistryKey.Handle`, `Environment.Is64BitOperatingSystem`. The compiler tells you.
- `List.Sort` is unstable: for Python `sorted` always use `Py.SortBy` or LINQ `OrderBy`.
- Python `%` with numbers, `round()` (banker's rounding in Py3, same as the .NET `Math.Round` default), float output: `Json` writes doubles like Python (`1.0`, `0.1`).
- Time: `time.strftime` is local, `os.path.getmtime` = `File.GetLastWriteTime`. File name order of `os.listdir` (NTFS: alphabetical, like `Directory.GetFileSystemEntries`).
- Python `str.lower()` = `ToLowerInvariant()`. Regex: Python `re` ≈ .NET Regex, but `\d` in .NET matches Unicode digits (doesn't matter here), `$` matches before `\n` in both.
- COM (IShellLink, IPersistFile): client threads are STA. `[ComImport]` interfaces or vtable calls as in the Python code.
- `zlib.compress`: .NET 3.5 only has `DeflateStream` (raw). zlib = 2 byte header (0x78 0x9C) + deflate + Adler-32 (big endian).
- The app's process name is `xpserver.exe` (already listed in MOUNT_SAFE_FG).

## Testing on an XP machine

- Build: `cd server && dotnet build -c Release` -> `server/bin/Release/net35/xpserver.exe`.
- Each group gets its own test port (5051 to 5059) and folder on the XP machine, running next to the reference server on 5050. Open the firewall port first. Setup:
  `client/xpctl.py put server/bin/Release/net35/xpserver.exe 'C:\xpctl-net\<group>\' --mkdir`,
  `client/xpctl.py run 'copy /y C:\xpctl-net\core\xpserver.json C:\xpctl-net\<group>\ & copy /y C:\xpctl\xpctl.key C:\xpctl-net\<group>\'`,
  start: `client/xpctl.py run --detach 'C:\xpctl-net\<group>\xpserver.exe --port <P> --no-watchdog'`.
  New version: stop your own process by PID (`client/xpctl.py ps xpserver --cmdline`, then `client/xpctl.py kill <PID>`), copy the exe again, restart.
- Compare: `server/tests/parity.py --b <P> server/tests/cases/<group>.jsonl` (A = Python server on 5050). Cases as JSON lines,
  `_ignore` for fields that may change between two calls.
- `export XPCTL_HOST=<address of the XP machine>`. Password as for the client (`~/.config/xpctl/key`).

## Don'ts on the test machine

- Never stop or restart the Python server (port 5050, python.exe, xpserver.bat cmd.exe) or other xpserver.exe instances. Only your own process, by PID.
- Don't install or uninstall anything, no drivers, no NVIDIA settings, don't eject the CD drive, don't mount images,
  don't permanently change desktop shortcuts or icon positions, don't write or delete anything outside `C:\xpctl-net\<group>\`.
  Port such ops anyway, but only test them with harmless error cases or test objects in your own folder; report them as "ported, not tested live".
- Input ops (click/type/key/focus/close/press/settext) only against windows you started yourself (for example notepad), and close them afterwards.
- Don't change files outside `server/Ops/<Group>.cs` and `server/tests/cases/<group>.jsonl`. If something is missing from the scaffolding: report it, solve it locally in your own file.
