using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace XpServer
{
    /// <summary>Eine gefundene Spiele-Exe mit Datenbank-Treffer.</summary>
    public sealed class GameRow
    {
        public string Name, Exe, Source;        // Source: gog | reg | lnk
        public long Size = -1;
        public long Ticks;                      // Änderungszeit (UTC-Ticks), Schlüssel für den md5-Cache
        public string Md5;                      // null, solange nicht gehasht
        public bool Confirmed;                  // md5 stimmt mit dem Treffer überein
        public string Problem;                  // Exe fehlt, gesperrt, Exe verändert ...
        public string GogVer;                   // "ver" aus der GOG-Registry
        public GameDb.Entry Hit;

        /// <summary>aktuell | patch | unklar | unbekannt | fehlt</summary>
        public string Status
        {
            get
            {
                if (Size < 0) return "fehlt";
                return Hit == null ? "unbekannt" : Hit.Status;
            }
        }
    }

    public sealed class GameScanResult
    {
        public readonly List<GameRow> Rows = new List<GameRow>();
        public GameDb Db;
        public DateTime When = DateTime.Now;
        public long FindMs, HashMs;
        public int Hashed, Cached, Pending;
        public bool HashDone;
    }

    /// <summary>
    /// Spiele finden, die die Datenbank kennt: zuerst GOG-Registry (ein Schlüssel für alle GOG-Spiele),
    /// dann Registry-Hinweise der Datenbank (Blizzard, EA ...), dann Desktop-Verknüpfungen. Erste
    /// Zuordnung nur über Exe-Name + Größe (kein Lesen der Datei), danach md5 im Hintergrund zur
    /// Bestätigung, mit Cache (Pfad, Größe, Zeit) in xpserver-md5.tsv neben der Exe.
    /// Unbekannte Exes werden nicht gehasht.
    /// </summary>
    public static class GameScan
    {
        const string GogKey = @"SOFTWARE\GOG.com\Games";
        static readonly object RunLock = new object();
        static readonly object CacheLock = new object();
        static Dictionary<string, string[]> _cache;   // pfad (klein) -> [size, ticks, md5]
        public static volatile GameScanResult Last;

        static string CachePath { get { return Path.Combine(Program.Here, "xpserver-md5.tsv"); } }

        /// <summary>Findet die Spiele (schnell, ohne Hash). Wirft nichts außer bei unlesbarer Datenbank.</summary>
        public static GameScanResult Find()
        {
            var sw = Stopwatch.StartNew();
            var res = new GameScanResult { Db = GameDb.Get() };
            var db = res.Db;
            var seen = new Dictionary<string, GameRow>(StringComparer.OrdinalIgnoreCase);

            // 1. GOG: alle Spiele unter einem Schlüssel, mit path, exe und ver
            foreach (var id in Safe(() => Reg.Subs("HKLM", GogKey), new List<string>()))
            {
                var entries = db.ByGog(id);
                if (entries.Count == 0) continue;
                J v = Safe(() => Reg.Vals("HKLM", GogKey + "\\" + id), new J());
                string dir = Str(v, "path"), regExe = Str(v, "exe"), ver = Str(v, "ver");
                foreach (var e in entries)
                {
                    string exe = null;
                    // nur vorhandene Dateien: die GOG-Registry nennt manchmal eine Exe, die woanders liegt
                    if (regExe != null && string.Equals(Path.GetFileName(regExe), e.Exe, StringComparison.OrdinalIgnoreCase) && File.Exists(regExe)) exe = regExe;
                    else if (dir != null && File.Exists(Path.Combine(dir, e.Exe))) exe = Path.Combine(dir, e.Exe);
                    if (exe == null) continue;
                    Add(res, seen, e.Game, exe, "gog", ver);
                }
            }

            // 2. Registry-Hinweise aus der Datenbank
            foreach (var e in db.All)
            {
                foreach (var hint in e.Reg)
                {
                    string dir = RegDir(hint);
                    if (dir == null) continue;
                    string exe = Path.Combine(dir, e.Exe);
                    if (File.Exists(exe)) { Add(res, seen, e.Game, exe, "reg", null); break; }
                }
            }

            // 3. Desktop-Verknüpfungen
            LinkMap links = Safe(() => Shell.AllLinks(), null);
            if (links != null)
                foreach (var name in links.Keys)
                {
                    J g = Safe(() => Shell.LinkGame(name, links[name]), null);
                    string exe = g != null ? g["exe"] as string : null;
                    if (string.IsNullOrEmpty(exe)) continue;
                    string low = exe.ToLowerInvariant();
                    if (!low.EndsWith(".exe") || low.Contains("\\windows\\")) continue;
                    Add(res, seen, name, exe, "lnk", null);
                }

            res.Rows.Sort((a, b) => Order(a).CompareTo(Order(b)) != 0 ? Order(a).CompareTo(Order(b)) :
                string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            res.FindMs = sw.ElapsedMilliseconds;
            return res;
        }

        static int Order(GameRow r)
        {
            switch (r.Status) { case "patch": return 0; case "unklar": return 1; case "fehlt": return 2; case "aktuell": return 3; default: return 4; }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint size);

        /// <summary>Lange Form eines Pfads (C:\Games\NFSUND~1 -> C:\Games\NFS Underground), damit Funde aus
        /// Registry und Verknüpfung als dieselbe Datei erkannt werden. Fehlt die Datei, bleibt der Pfad.</summary>
        static string LongPath(string p)
        {
            try
            {
                var sb = new StringBuilder(520);
                uint n = GetLongPathNameW(p, sb, (uint)sb.Capacity);
                return n > 0 && n < sb.Capacity ? sb.ToString() : p;
            }
            catch { return p; }
        }

        static void Add(GameScanResult res, Dictionary<string, GameRow> seen, string name, string exe, string source, string gogVer)
        {
            exe = LongPath(exe);
            GameRow row;
            if (seen.TryGetValue(exe, out row))
            {
                if (row.GogVer == null) row.GogVer = gogVer;
                return;
            }
            row = new GameRow { Name = name, Exe = exe, Source = source, GogVer = gogVer };
            seen[exe] = row;
            Stat(row);
            if (row.Size >= 0)
            {
                row.Hit = res.Db.ByExeSize(Path.GetFileName(exe), row.Size, name);
                // Launcher (z. B. Diablo II.exe): die eigentliche Version steckt in einer anderen Datei
                if (row.Hit != null && row.Hit.Probe != null)
                {
                    string probe = LongPath(Path.Combine(Path.GetDirectoryName(exe), row.Hit.Probe));
                    var tmp = new GameRow { Exe = probe };
                    Stat(tmp);
                    var hit = tmp.Size >= 0 ? res.Db.ByExeSize(row.Hit.Probe, tmp.Size, name) : null;
                    if (hit != null)
                    {
                        GameRow known;
                        if (seen.TryGetValue(probe, out known)) return;   // schon über die Registry gefunden
                        row.Exe = probe; row.Size = tmp.Size; row.Ticks = tmp.Ticks; row.Hit = hit; seen[probe] = row;
                    }
                }
                if (row.Hit != null && source != "lnk") row.Name = row.Hit.Game;
                lock (CacheLock)
                {
                    LoadCache();
                    string[] c;
                    if (_cache.TryGetValue(row.Exe.ToLowerInvariant(), out c) && c[0] == row.Size.ToString(CultureInfo.InvariantCulture) &&
                        c[1] == row.Ticks.ToString(CultureInfo.InvariantCulture))
                    {
                        Confirm(res.Db, row, c[2]);
                        res.Cached++;
                    }
                }
            }
            else row.Problem = "Exe fehlt";
            res.Rows.Add(row);
        }

        static void Stat(GameRow r)
        {
            try
            {
                var fi = new FileInfo(r.Exe);
                if (!fi.Exists) { r.Size = -1; return; }
                r.Size = fi.Length;
                r.Ticks = fi.LastWriteTimeUtc.Ticks;
            }
            catch { r.Size = -1; }
        }

        /// <summary>md5 eintragen und Treffer bestätigen oder korrigieren.</summary>
        static void Confirm(GameDb db, GameRow r, string md5)
        {
            r.Md5 = md5;
            if (r.Hit == null) return;
            if (r.Hit.Md5 == md5) { r.Confirmed = true; return; }
            var other = db.ByMd5(md5, r.Name);
            r.Hit = other;
            r.Confirmed = other != null;
            if (other == null) r.Problem = "Exe verändert (gleiche Größe, anderer md5)";
        }

        /// <summary>Hasht alle bekannten, noch nicht bestätigten Exes. cancel: abbrechen; pause: warten (Vollbild).</summary>
        public static void Hash(GameScanResult res, Func<bool> cancel, Func<bool> pause, Action<GameRow> done)
        {
            var sw = Stopwatch.StartNew();
            var todo = res.Rows.FindAll(r => r.Hit != null && r.Md5 == null && r.Size >= 0);
            res.Pending = todo.Count;
            var buf = new byte[64 * 1024];
            foreach (var r in todo)
            {
                while (pause != null && pause()) { if (cancel != null && cancel()) return; Thread.Sleep(1000); }
                if (cancel != null && cancel()) return;
                try
                {
                    string md5;
                    using (var f = new FileStream(r.Exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, buf.Length))
                    using (var h = MD5.Create())
                    {
                        int n;
                        while ((n = f.Read(buf, 0, buf.Length)) > 0) h.TransformBlock(buf, 0, n, null, 0);
                        h.TransformFinalBlock(buf, 0, 0);
                        md5 = Server.Hex(h.Hash);
                    }
                    Confirm(res.Db, r, md5);
                    lock (CacheLock) { LoadCache(); _cache[r.Exe.ToLowerInvariant()] = new[] { r.Size.ToString(CultureInfo.InvariantCulture), r.Ticks.ToString(CultureInfo.InvariantCulture), md5 }; }
                    res.Hashed++;
                }
                catch (Exception e) { r.Problem = "nicht lesbar: " + e.Message; }
                res.Pending--;
                if (done != null) done(r);
            }
            res.HashDone = true;
            res.HashMs = sw.ElapsedMilliseconds;
            SaveCache();
        }

        /// <summary>Ein kompletter Lauf (für den Op und den Reiter), nie zwei gleichzeitig.</summary>
        public static GameScanResult Run(bool hash, Func<bool> cancel, Func<bool> pause, Action<GameScanResult> found, Action<GameRow> done)
        {
            lock (RunLock)
            {
                var res = Find();
                Last = res;
                if (found != null) found(res);
                if (hash) Hash(res, cancel, pause, done);
                Log.Write(string.Format("  Spiele-Scan: {0} Exes, {1} bekannt, Suche {2} ms, {3} gehasht in {4} ms, {5} aus dem Cache",
                    res.Rows.Count, res.Rows.FindAll(r => r.Hit != null).Count, res.FindMs, res.Hashed, res.HashMs, res.Cached), LogKind.System);
                return res;
            }
        }

        // ---------- md5-Cache: Zeilen "pfad<TAB>größe<TAB>ticks<TAB>md5" ----------

        static void LoadCache()
        {
            if (_cache != null) return;
            _cache = new Dictionary<string, string[]>();
            try
            {
                if (!File.Exists(CachePath)) return;
                foreach (var line in File.ReadAllLines(CachePath, Encoding.UTF8))
                {
                    var p = line.Split('\t');
                    if (p.Length == 4 && p[3].Length == 32) _cache[p[0]] = new[] { p[1], p[2], p[3] };
                }
            }
            catch (Exception e) { Log.Write("  md5-Cache unlesbar, fange neu an: " + e.Message, LogKind.Error); _cache.Clear(); }
        }

        static void SaveCache()
        {
            try
            {
                var sb = new StringBuilder();
                lock (CacheLock)
                {
                    if (_cache == null) return;
                    foreach (var kv in _cache) sb.Append(kv.Key).Append('\t').Append(kv.Value[0]).Append('\t').Append(kv.Value[1]).Append('\t').Append(kv.Value[2]).Append("\r\n");
                }
                string tmp = CachePath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(CachePath)) File.Delete(CachePath);
                File.Move(tmp, CachePath);
            }
            catch (Exception e) { Log.Write("  md5-Cache nicht gespeichert: " + e.Message, LogKind.Error); }
        }

        // ---------- Hilfen ----------

        static string RegDir(string hint)
        {
            int bar = hint.LastIndexOf('|');
            if (bar < 0) return null;
            string full = hint.Substring(0, bar), name = hint.Substring(bar + 1), hive, path;
            Reg.Split(full, out hive, out path);
            object v = Safe(() => Reg.Get(hive, path, name), null);
            string s = v as string;
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Trim().TrimEnd('\\');
            return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(s) : s;
        }

        static string Str(J v, string k)
        {
            object o;
            if (v == null || !v.TryGetValue(k, out o) || o == null) return null;
            // Reg.Vals liefert {name: {type, data}} oder direkt den Wert
            var j = o as J;
            if (j != null) { object d; return j.TryGetValue("data", out d) && d != null ? d.ToString() : null; }
            string s = o.ToString();
            return s.Length > 0 ? s : null;
        }

        static T Safe<T>(Func<T> f, T def)
        {
            try { return f(); }
            catch (EndOfConnection) { throw; }
            catch { return def; }
        }

        // ---------- Op ----------

        /// <summary>gamescan: bekannte Spiele mit Version und Status. hash=false: nur die schnelle Zuordnung.</summary>
        [Op("gamescan")]
        static J ScanOp(Conn c, Req r)
        {
            bool hash = !r.Has("hash") || Py.Truthy(r["hash"]);
            var prio = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            GameScanResult res;
            try { res = Run(hash, null, null, null, null); }
            finally { Thread.CurrentThread.Priority = prio; }
            var rows = new List<object>();
            foreach (var x in res.Rows)
            {
                var h = x.Hit;
                rows.Add(J.O("name", x.Name, "exe", x.Exe, "source", x.Source, "size", x.Size, "md5", x.Md5,
                    "confirmed", x.Confirmed, "status", x.Status, "problem", x.Problem, "gog_ver", x.GogVer,
                    "game", h != null ? h.Game : null, "version", h != null ? h.Version : null,
                    "latest", h != null ? h.Latest : null, "confidence", h != null ? h.Confidence : null));
            }
            return J.Ok("games", rows, "stand", res.Db.Stand, "db_source", res.Db.Source, "db_entries", res.Db.All.Count,
                "db_ms", res.Db.LoadMs, "find_ms", res.FindMs, "hash_ms", res.HashMs, "hashed", res.Hashed, "cached", res.Cached);
        }
    }
}
