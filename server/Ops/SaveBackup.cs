using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace XpServer.Ops
{
    /// <summary>
    /// Spielstände sichern: pro Spiel aus client/save-locations.json ein normales Zip
    /// <c>&lt;ziel&gt;\&lt;id&gt;\&lt;id&gt;_JJJJ-MM-TT_hh-mm-ss.zip</c> mit allen Dateien des Spielstand-Ordners
    /// (samt Unterordnern) und info.json. Ein neues Zip nur, wenn sich eine Datei geändert hat (CRC und
    /// Größe gegen das letzte Zip). Der Zielordner muss existieren, sonst bricht die Sicherung ab; angelegt
    /// wird nur der Unterordner pro Spiel. Es läuft immer nur eine Sicherung (Knopf und Op teilen sich das).
    /// Die Arbeit hängt nicht an der Oberfläche: das Fenster hört nur auf <see cref="Job.Changed"/>.
    /// </summary>
    public static class SaveBackup
    {
        const string UiKey = @"Software\xpctl\xpserver";
        const string TargetValue = "backup_dir";

        /// <summary>Gestartete Sicherung (für das Fortschrittsfenster). bool: per Befehl gestartet.</summary>
        public static event Action<Job, bool> Started;
        /// <summary>Sicherung fertig (aus dem Arbeits-Thread), z. B. um die Übersicht neu zu laden.</summary>
        public static event Action<Job> Finished;

        static readonly object BusyLock = new object();
        static Job _running;
        public static Job Running { get { lock (BusyLock) return _running; } }

        // ---------- Zielordner (HKCU, wie die übrigen Fenster-Einstellungen) ----------

        public static string Target
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(UiKey))
                    {
                        var v = k == null ? null : k.GetValue(TargetValue) as string;
                        return string.IsNullOrEmpty(v) ? null : v;
                    }
                }
                catch { return null; }
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(UiKey))
                {
                    if (string.IsNullOrEmpty(value)) k.DeleteValue(TargetValue, false);
                    else k.SetValue(TargetValue, value, RegistryValueKind.String);
                }
            }
        }

        /// <summary>Prüft einen Zielordner: absolut (Laufwerk oder UNC). Liefert den bereinigten Pfad.</summary>
        public static string CleanTarget(string p)
        {
            p = (p ?? "").Trim().Trim('"');
            if (p.Length == 0) return "";
            bool drive = p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && p[2] == '\\';
            bool unc = p.StartsWith(@"\\") && p.Length > 2;
            if (!drive && !unc) throw new OpError("Zielordner muss ein absoluter Pfad sein (z. B. Z:\\saves oder \\\\vault\\saves): " + p);
            return p.Length > 3 ? p.TrimEnd('\\') : p;
        }

        // ---------- Speicherorte ----------

        public sealed class Loc
        {
            public string Id, Title, Exe;
            public List<string> Processes = new List<string>(), Install = new List<string>(), Saves = new List<string>();
        }

        public const string DbName = "save-locations.json";
        static List<Loc> _db;
        static string _dbStand;

        public static List<Loc> Db()
        {
            lock (BusyLock)
            {
                if (_db != null) return _db;
                string emb, ext = null;
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(DbName))
                {
                    if (s == null) throw new OpError(DbName + " fehlt in der Exe");
                    using (var r = new StreamReader(s, Encoding.UTF8)) emb = r.ReadToEnd();
                }
                string extPath = Path.Combine(Program.Here, DbName);
                if (File.Exists(extPath)) try { ext = File.ReadAllText(extPath, Encoding.UTF8); } catch { }
                var a = Parse(emb);
                J b = null;
                if (ext != null) try { b = Json.Parse(ext) as J; } catch (Exception e) { Log.Write("  " + DbName + " neben der Exe unlesbar: " + e.Message, LogKind.Error); }
                var pick = b != null && string.CompareOrdinal(S(b, "stand"), S(a, "stand")) > 0 ? b : a;
                _dbStand = S(pick, "stand");
                var l = new List<Loc>();
                var games = pick["games"] as List<object>;
                if (games != null)
                    foreach (var o in games)
                    {
                        var g = o as J;
                        if (g == null) continue;
                        var x = new Loc { Id = S(g, "id"), Title = S(g, "title"), Exe = S(g, "exe") };
                        Strs(g, "processes", x.Processes);
                        Strs(g, "install", x.Install);
                        Strs(g, "saves", x.Saves);
                        if (!string.IsNullOrEmpty(x.Id)) l.Add(x);
                    }
                _db = l;
                return l;
            }
        }

        static J Parse(string text)
        {
            var j = Json.Parse(text) as J;
            if (j == null) throw new OpError(DbName + ": oberste Ebene muss ein Objekt sein");
            return j;
        }

        static string S(J o, string k) { object v; return o != null && o.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : ""; }
        static void Strs(J o, string k, List<string> into)
        {
            object v;
            if (o.TryGetValue(k, out v) && v is List<object> l) foreach (var x in l) into.Add(Convert.ToString(x, CultureInfo.InvariantCulture));
        }

        static string RegValue(string spec)
        {
            int bar = spec.LastIndexOf('|');
            if (bar < 0) return null;
            string hive, path;
            Reg.Split(spec.Substring(0, bar), out hive, out path);
            try
            {
                var v = Reg.Get(hive, path, spec.Substring(bar + 1));
                var s = v as string;
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch { return null; }
        }

        static string FromRegValue(string v, string install)
        {
            v = v.Trim().TrimEnd('\\');
            if (v.Length >= 2 && v[1] == ':') return v;
            if (v.StartsWith("\\")) return install.Substring(0, 2) + v;
            return Path.Combine(install, v);
        }

        /// <summary>Installations- und Spielstand-Ordner; install null = nicht installiert, save null = kein Ordner gefunden.</summary>
        public static void Locate(Loc g, out string install, out string save)
        {
            install = null; save = null;
            foreach (var spec in g.Install)
            {
                var v = RegValue(spec);
                if (v != null) { install = v.TrimEnd('\\'); break; }
            }
            if (install == null) return;
            foreach (var cand in g.Saves)
            {
                string p;
                if (cand.StartsWith("reg:"))
                {
                    var v = RegValue(cand.Substring(4));
                    if (v == null) continue;
                    p = FromRegValue(v, install);
                }
                else
                {
                    p = cand.Replace("{install}", install)
                            .Replace("{common_appdata}", Common.FolderPath(0x23))
                            .Replace("{appdata}", Common.FolderPath(0x1A))
                            .Replace("{personal}", Common.FolderPath(0x05));
                }
                if (Directory.Exists(p)) { save = p; return; }
            }
        }

        // ---------- Sicherung ----------

        public sealed class GameResult
        {
            public string Id, Title, Status = "wartet", Zip, SaveDir, InstallDir, Error, Version;
            public int Files;
            public long Size, ZipSize;
            public List<string> Running = new List<string>();

            public J ToJ()
            {
                var j = J.O("game", Id, "title", Title, "status", Status);
                if (SaveDir != null) j["save_dir"] = SaveDir;
                if (InstallDir != null) j["install_dir"] = InstallDir;
                if (Version != null) j["version"] = Version;
                if (Zip != null) j["zip"] = Zip;
                if (Status == "neu" || Status == "unverändert" || Status == "installiert") { j["files"] = Files; j["size"] = Size; }
                if (Status == "neu") j["zip_size"] = ZipSize;
                if (Running.Count > 0) j["running"] = new List<object>(Running.ConvertAll(x => (object)x));
                if (Error != null) j["error"] = Error;
                return j;
            }
        }

        public sealed class Job
        {
            public readonly List<GameResult> Games = new List<GameResult>();
            public string Dest, Error;
            public bool Done, ListOnly;
            public DateTime When = DateTime.Now;
            /// <summary>Etwas hat sich geändert (aus dem Arbeits-Thread, Empfänger muss selbst auf die Oberfläche wechseln).</summary>
            public event Action<Job> Changed;
            internal void Fire() { var h = Changed; if (h != null) try { h(this); } catch { } }

            public J ToJ()
            {
                var l = new List<object>();
                foreach (var g in Games) l.Add(g.ToJ());
                return J.Ok("dest", Dest, "list", ListOnly, "games", l);
            }
        }

        /// <summary>Sicherung starten und abwarten. Wirft OpError bei unbekanntem Spiel, fehlendem oder nicht
        /// vorhandenem Zielordner und wenn schon eine Sicherung läuft.</summary>
        public static Job Run(List<string> ids, string destOverride, bool listOnly, bool remote, bool showDialog)
        {
            var db = Db();
            var pick = new List<Loc>();
            if (ids == null || ids.Count == 0) pick.AddRange(db);
            else
                foreach (var id in ids)
                {
                    var g = db.Find(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                    if (g == null) throw new OpError("unbekanntes Spiel " + id + ". Bekannt: " + string.Join(", ", db.ConvertAll(x => x.Id).ToArray()));
                    pick.Add(g);
                }
            string dest = destOverride != null ? CleanTarget(destOverride) : Target;
            if (string.IsNullOrEmpty(dest) && !listOnly)
                throw new OpError("kein Zielordner eingestellt (xpserver-Fenster, Reiter Sicherung, oder xpctl.py saves target ORDNER)");
            if (!listOnly && !Directory.Exists(dest))
                throw new OpError("Zielordner gibt es nicht: " + dest + " (Laufwerk verbunden, Freigabe erreichbar?). Nichts gesichert.");

            var job = new Job { Dest = dest, ListOnly = listOnly };
            foreach (var g in pick) job.Games.Add(new GameResult { Id = g.Id, Title = g.Title });
            if (!listOnly)                                // nur anzeigen sperrt nichts
                lock (BusyLock)
                {
                    if (_running != null) throw new OpError("es läuft schon eine Sicherung (seit " + _running.When.ToString("HH:mm:ss") + ")");
                    _running = job;
                }
            try
            {
                if (showDialog && !listOnly) { var h = Started; if (h != null) try { h(job, remote); } catch { } }
                for (int i = 0; i < pick.Count; i++)
                {
                    var r = job.Games[i];
                    r.Status = listOnly ? "suche" : "sichert";
                    job.Fire();
                    try { One(pick[i], r, dest, listOnly); }
                    catch (Exception e)
                    {
                        r.Status = "fehler";
                        r.Error = e is OpError ? e.Message : e.GetType().Name + ": " + e.Message;
                        Log.Write("  Sicherung " + r.Id + ": " + r.Error, LogKind.Error);
                    }
                    job.Fire();
                }
            }
            finally
            {
                job.Done = true;
                if (!listOnly) lock (BusyLock) if (_running == job) _running = null;
                job.Fire();
                if (!listOnly) { var h = Finished; if (h != null) try { h(job); } catch { } }
            }
            return job;
        }

        static readonly Regex Stamp = new Regex(@"^(.+)_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}\.zip$", RegexOptions.IgnoreCase);

        /// <summary>Neuestes Zip dieses Spiels (strenger Name, .part und fremde Dateien zählen nicht).</summary>
        public static string Latest(string dir, string id)
        {
            if (!Directory.Exists(dir)) return null;
            string best = null;
            foreach (var f in Directory.GetFiles(dir, id + "_*.zip"))
            {
                var name = Path.GetFileName(f);
                var m = Stamp.Match(name);
                if (!m.Success || !string.Equals(m.Groups[1].Value, id, StringComparison.OrdinalIgnoreCase)) continue;
                if (best == null || string.CompareOrdinal(name, Path.GetFileName(best)) > 0) best = f;
            }
            return best;
        }

        static byte[] ReadShared(string p)
        {
            using (var f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var b = new byte[f.Length];
                int off = 0, n;
                while (off < b.Length && (n = f.Read(b, off, b.Length - off)) > 0) off += n;
                if (off != b.Length) throw new IOException(p + ": kürzer als erwartet");
                return b;
            }
        }

        static List<string> RunningOf(Loc g)
        {
            var r = new List<string>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses()) { try { names.Add(p.ProcessName + ".exe"); } catch { } finally { p.Dispose(); } }
            foreach (var want in g.Processes) if (names.Contains(want)) r.Add(want);
            return r;
        }

        static string Offset(DateTime local)
        {
            var o = TimeZone.CurrentTimeZone.GetUtcOffset(local);
            return (o < TimeSpan.Zero ? "-" : "+") + Math.Abs(o.Hours).ToString("00") + ":" + Math.Abs(o.Minutes).ToString("00");
        }

        static void One(Loc g, GameResult r, string dest, bool listOnly)
        {
            string install, save;
            Locate(g, out install, out save);
            if (install == null) { r.Status = "nicht installiert"; return; }
            r.InstallDir = install;
            r.SaveDir = save;
            string exeMd5 = null;
            if (!string.IsNullOrEmpty(g.Exe))
            {
                string exe = Path.Combine(install, g.Exe);
                if (File.Exists(exe))
                    try { using (var h = MD5.Create()) exeMd5 = Server.Hex(h.ComputeHash(ReadShared(exe))); } catch { }
                if (exeMd5 != null)
                {
                    GameDb.Entry hit = null;
                    try { hit = GameDb.Get().ByMd5(exeMd5, g.Title); } catch { }
                    r.Version = hit != null ? hit.Version : "unbekannte Exe (md5 " + exeMd5.Substring(0, 12) + ")";
                }
            }
            if (save == null) { r.Status = "leer"; r.Error = "kein Spielstand-Ordner gefunden"; return; }

            var files = new List<string>(Directory.GetFiles(save, "*", SearchOption.AllDirectories));
            files.Sort((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
            r.Files = files.Count;
            r.Size = 0;
            foreach (var f in files) r.Size += new FileInfo(f).Length;
            string dir = string.IsNullOrEmpty(dest) ? null : Path.Combine(dest, g.Id);
            if (listOnly)
            {
                r.Status = "installiert";
                var last = dir != null && Directory.Exists(dest) ? Latest(dir, g.Id) : null;
                if (last != null) r.Zip = last;
                return;
            }
            if (files.Count == 0) { r.Status = "leer"; return; }

            // lesen
            var names = new List<string>();
            var datas = new List<byte[]>();
            var times = new List<DateTime>();
            foreach (var f in files)
            {
                names.Add(f.Substring(save.TrimEnd('\\').Length + 1).Replace('\\', '/'));
                datas.Add(ReadShared(f));
                times.Add(File.GetLastWriteTime(f));
            }
            r.Size = 0;
            foreach (var d in datas) r.Size += d.Length;

            // unverändert?
            var prev = Latest(dir, g.Id);
            if (prev != null)
            {
                List<Zip.Entry> old;
                try { old = Zip.List(File.ReadAllBytes(prev)); }
                catch (Exception e) { throw new OpError(prev + " ist kein lesbares Zip (" + e.Message + "). Prüfen und beiseitelegen, dann noch einmal."); }
                var want = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int i = 0; i < names.Count; i++) want[names[i]] = Zip.Crc32(datas[i]).ToString("x8") + "/" + datas[i].Length;
                var have = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var e in old) if (e.Name != "info.json" && !e.Name.EndsWith("/")) have[e.Name] = e.Crc.ToString("x8") + "/" + e.Size;
                bool same = want.Count == have.Count;
                if (same) foreach (var kv in want) { string v; if (!have.TryGetValue(kv.Key, out v) || v != kv.Value) { same = false; break; } }
                if (same) { r.Status = "unverändert"; r.Zip = prev; return; }
            }

            r.Running = RunningOf(g);
            var when = DateTime.Now;
            var fileList = new List<object>();
            using (var h = MD5.Create())
                for (int i = 0; i < names.Count; i++)
                    fileList.Add(J.O("path", names[i], "size", datas[i].Length,
                        "mtime", times[i].ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        "md5", Server.Hex(h.ComputeHash(datas[i]))));
            var info = J.O(
                "game", g.Id, "title", g.Title, "version", r.Version, "exe", g.Exe, "exe_md5", exeMd5,
                "install_dir", install, "save_dir", save,
                "created", when.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + Offset(when),
                "created_by", "xpserver v" + Program.Version + " (savebackup)",
                "computer", Environment.MachineName,
                "running", new List<object>(r.Running.ConvertAll(x => (object)x)),
                "files", fileList);

            Directory.CreateDirectory(dir);
            string final = Path.Combine(dir, g.Id + "_" + when.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + ".zip");
            if (File.Exists(final)) throw new OpError(final + " gibt es schon (gleiche Sekunde, anderer Inhalt). In einer Sekunde noch einmal.");
            string part = final + ".part";
            if (File.Exists(part)) File.Delete(part);   // Rest eines abgebrochenen Laufs, nie ein gültiges Zip
            try
            {
                using (var fs = new FileStream(part, FileMode.CreateNew, FileAccess.Write))
                {
                    var w = new Zip.Writer(fs);
                    for (int i = 0; i < names.Count; i++) w.Add(names[i], datas[i], times[i]);
                    w.Add("info.json", new UTF8Encoding(false).GetBytes(Json.Dump(info) + "\r\n"), when);
                    w.Finish();
                }
                // prüfen: Zentralverzeichnis lesen, jeden Eintrag entpacken (CRC), mit dem Gelesenen vergleichen
                var bytes = File.ReadAllBytes(part);
                var entries = Zip.List(bytes);
                if (entries.Count != names.Count + 1) throw new IOException("Zip hat " + entries.Count + " Einträge statt " + (names.Count + 1));
                for (int i = 0; i < names.Count; i++)
                {
                    var e = entries[i];
                    var back = Zip.Read(bytes, e);
                    if (e.Name != names[i] || back.Length != datas[i].Length || Zip.Crc32(back) != Zip.Crc32(datas[i]))
                        throw new IOException(names[i] + ": beim Zurücklesen anders");
                }
                Zip.Read(bytes, entries[names.Count]);
                if (File.Exists(final)) throw new OpError(final + " ist während des Sicherns entstanden, nichts überschrieben");
                File.Move(part, final);
                r.ZipSize = bytes.Length;
            }
            finally
            {
                if (File.Exists(part)) try { File.Delete(part); } catch { }
            }
            r.Zip = final;
            r.Status = "neu";
            Log.Write(string.Format("  Sicherung {0}: {1} ({2} Dateien, {3} Byte)", g.Id, final, names.Count, r.ZipSize), LogKind.Info);
        }

        // ---------- Ops ----------

        [Op("savebackup")]
        static J SaveBackupOp(Conn c, Req r)
        {
            var ids = r.Has("games") ? r.Strs("games") : new List<string>();
            string dest = r.Has("dest") ? r.Str("dest") : null;
            bool dialog = !r.Has("dialog") || r.Bool("dialog");
            try { return Run(ids, dest, r.Bool("list"), true, dialog).ToJ(); }
            catch (OpError e) { return J.Err(e.Message); }   // Meldung für Menschen, ohne Python-Typ davor
        }

        [Op("savetarget")]
        static J SaveTargetOp(Conn c, Req r)
        {
            if (r.Has("set"))
                try { Target = CleanTarget(r.Str("set")); }
                catch (OpError e) { return J.Err(e.Message); }
            string t = Target;
            var j = J.Ok("dest", t, "exists", t != null && Directory.Exists(t));
            var db = Db();
            j["games"] = new List<object>(db.ConvertAll(x => (object)x.Id));
            j["db_stand"] = _dbStand;
            return j;
        }
    }
}
