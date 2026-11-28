using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace XpServer.Ops
{
    /// <summary>
    /// Spielstand zurückholen: einen Ordner aus einem Sicherungs-Zip wiederherstellen (Op saverestore,
    /// Reiter Sicherung). Ablauf: Spiel darf nicht laufen, Zip komplett lesen und prüfen, info.json muss zum
    /// Spiel passen, dann den aktuellen Stand sichern (wie savebackup; schlägt das fehl, passiert nichts),
    /// danach den Ordner auf den Inhalt des Zips setzen (siehe <see cref="Restore"/>) und Byte für Byte
    /// mit dem Zip vergleichen. Teilt sich die Sperre mit der Sicherung: es läuft immer nur eins von beiden.
    /// </summary>
    public static partial class SaveBackup
    {
        public sealed class Stand
        {
            public string Name, Path, When;
            public long Size;
            public int Files = -1;       // -1: Zip unlesbar

            public J ToJ() { return J.O("name", Name, "path", Path, "when", When, "size", Size, "files", Files); }
        }

        public sealed class RestoreResult
        {
            public string Game, Title, Zip, SaveDir, ZipSaveDir, BackupStatus, BackupZip;
            public bool DryRun;
            public Restore.Result Changes;

            public J ToJ()
            {
                var j = J.Ok("game", Game, "title", Title, "zip", Zip, "save_dir", SaveDir, "dry_run", DryRun,
                    "written", L(Changes.Written), "removed", L(Changes.Removed), "same", Changes.Same.Count);
                if (ZipSaveDir != null && !string.Equals(ZipSaveDir, SaveDir, StringComparison.OrdinalIgnoreCase)) j["zip_save_dir"] = ZipSaveDir;
                if (BackupStatus != null) j["backup"] = J.O("status", BackupStatus, "zip", BackupZip);
                if (!DryRun) j["verified"] = true;
                return j;
            }

            static List<object> L(List<string> l) { return l.ConvertAll(x => (object)x); }
        }

        static Loc Find(string id)
        {
            var db = Db();
            var g = db.Find(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (g == null) throw new OpError("unbekanntes Spiel " + id + ". Bekannt: " + string.Join(", ", db.ConvertAll(x => x.Id).ToArray()));
            return g;
        }

        static string Dest(string destOverride)
        {
            string dest = destOverride != null ? CleanTarget(destOverride) : Target;
            if (string.IsNullOrEmpty(dest)) throw new OpError("kein Zielordner eingestellt (xpserver-Fenster, Reiter Sicherung, oder xpctl.py saves target ORDNER)");
            if (!Directory.Exists(dest)) throw new OpError("Zielordner gibt es nicht: " + dest + " (Laufwerk verbunden, Freigabe erreichbar?)");
            return dest;
        }

        /// <summary>Sicherungen eines Spiels im Zielordner, neueste zuerst.</summary>
        public static List<Stand> Stands(string id, string destOverride)
        {
            var g = Find(id);
            string dir = Path.Combine(Dest(destOverride), g.Id);
            var l = new List<Stand>();
            if (!Directory.Exists(dir)) return l;
            foreach (var f in Directory.GetFiles(dir, g.Id + "_*.zip"))
            {
                var name = Path.GetFileName(f);
                var m = Stamp.Match(name);
                if (!m.Success || !string.Equals(m.Groups[1].Value, g.Id, StringComparison.OrdinalIgnoreCase)) continue;
                var s = new Stand { Name = name, Path = f, Size = new FileInfo(f).Length };
                string st = name.Substring(g.Id.Length + 1, 19);            // JJJJ-MM-TT_hh-mm-ss
                s.When = st.Substring(0, 10) + " " + st.Substring(11).Replace('-', ':');
                try { s.Files = Zip.List(File.ReadAllBytes(f)).FindAll(e => e.Name != Restore.InfoName && !e.Name.EndsWith("/")).Count; } catch { }
                l.Add(s);
            }
            l.Sort((a, b) => string.CompareOrdinal(b.Name, a.Name));
            return l;
        }

        /// <summary>Zip zu einer Angabe: voller Dateiname oder Anfang des Zeitstempels (2026-11-27_21 ...),
        /// muss eindeutig sein.</summary>
        public static Stand Pick(List<Stand> stands, string id, string want)
        {
            want = (want ?? "").Trim();
            if (want.Length == 0) throw new OpError("welcher Stand? Zip-Name oder Zeitstempel angeben");
            var hits = stands.FindAll(s => want.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? string.Equals(s.Name, Path.GetFileName(want), StringComparison.OrdinalIgnoreCase)
                : s.Name.Substring(id.Length + 1).StartsWith(want.Replace(' ', '_').Replace(':', '-'), StringComparison.Ordinal));
            if (hits.Count == 1) return hits[0];
            if (hits.Count == 0) throw new OpError("keine Sicherung von " + id + " passt zu " + want + " (xpctl.py saves restore " + id + " zeigt die Liste)");
            throw new OpError(want + " passt auf " + hits.Count + " Sicherungen: " + string.Join(", ", hits.ConvertAll(s => s.Name).ToArray()));
        }

        static DateTime? Mtime(object v)
        {
            DateTime t;
            var s = v as string;
            if (s != null && DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return t;
            return null;
        }

        /// <summary>Stand zurückholen. Wirft OpError für alles, was vor dem Schreiben auffällt (dann hat sich
        /// nichts geändert) und mit Hinweis auf die Vorher-Sicherung, wenn danach etwas schiefgeht.</summary>
        public static RestoreResult RunRestore(string id, string want, string destOverride, bool dryRun)
        {
            var g = Find(id);
            string dest = Dest(destOverride);
            var stand = Pick(Stands(g.Id, dest), g.Id, want);

            string install, save;
            Locate(g, out install, out save);
            if (install == null) throw new OpError(g.Title + " ist nicht installiert (Registry kennt keinen Installationsordner)");
            if (save == null) throw new OpError("kein Spielstand-Ordner von " + g.Title + " gefunden. Spiel einmal starten, dann noch einmal.");

            // Zip lesen und prüfen, bevor irgendetwas angefasst wird
            List<Restore.File1> files;
            byte[] infoBytes;
            try { files = Restore.Load(File.ReadAllBytes(stand.Path), out infoBytes); }
            catch (IOException e) { throw new OpError(stand.Name + ": " + e.Message + ". Nichts geändert."); }
            if (infoBytes == null) throw new OpError(stand.Name + " hat kein info.json (nicht von xpserver gesichert). Nichts geändert.");
            var info = Json.Parse(new UTF8Encoding(false).GetString(infoBytes).TrimStart('﻿')) as J;
            if (info == null) throw new OpError(stand.Name + ": info.json ist kein Objekt. Nichts geändert.");
            if (!string.Equals(S(info, "game"), g.Id, StringComparison.OrdinalIgnoreCase))
                throw new OpError(stand.Name + " gehört laut info.json zu '" + S(info, "game") + "', nicht zu " + g.Id + ". Nichts geändert.");
            if (files.Count == 0) throw new OpError(stand.Name + " enthält keine Dateien. Nichts geändert.");
            var times = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            object fl;
            if (info.TryGetValue("files", out fl) && fl is List<object>)
                foreach (var o in (List<object>)fl)
                {
                    var f = o as J;
                    object mt;
                    if (f == null || !f.TryGetValue("mtime", out mt)) continue;
                    var t = Mtime(mt);
                    if (t != null) times[S(f, "path")] = t.Value;
                }

            var res = new RestoreResult { Game = g.Id, Title = g.Title, Zip = stand.Path, SaveDir = save, ZipSaveDir = S(info, "save_dir"), DryRun = dryRun };
            if (dryRun)
            {
                res.Changes = Restore.Apply(save, files, times, true);
                return res;
            }

            var running = RunningOf(g);
            if (running.Count > 0) throw new OpError(string.Join(", ", running.ToArray()) + " läuft gerade. Erst das Spiel beenden, dann zurückholen. Nichts geändert.");

            var marker = new Job { Dest = dest };
            lock (BusyLock)
            {
                if (_running != null) throw new OpError("es läuft schon eine Sicherung oder Wiederherstellung (seit " + _running.When.ToString("HH:mm:ss") + ")");
                _running = marker;
            }
            try
            {
                // vorher sichern; ohne gültige Sicherung des jetzigen Stands wird nichts überschrieben
                var pre = new GameResult { Id = g.Id, Title = g.Title };
                try { One(g, pre, dest, false); }
                catch (Exception e) { throw new OpError("Sicherung vor dem Zurückholen fehlgeschlagen (" + (e is OpError ? e.Message : e.GetType().Name + ": " + e.Message) + "). Nichts geändert."); }
                if (pre.Status != "neu" && pre.Status != "unverändert" && pre.Status != "leer")
                    throw new OpError("Sicherung vor dem Zurückholen: " + pre.Status + (pre.Error != null ? " (" + pre.Error + ")" : "") + ". Nichts geändert.");
                res.BackupStatus = pre.Status;
                res.BackupZip = pre.Zip;
                string undo = pre.Zip != null ? " Stand vorher: " + pre.Zip : "";

                running = RunningOf(g);                 // könnte während der Sicherung gestartet worden sein
                if (running.Count > 0) throw new OpError(string.Join(", ", running.ToArray()) + " wurde gestartet. Nichts geändert." + undo);
                try { res.Changes = Restore.Apply(save, files, times); }
                catch (Exception e) { throw new OpError("Zurückholen abgebrochen: " + e.Message + ". Der Ordner kann halb geschrieben sein." + undo); }
                var bad = Restore.Verify(save, files);
                if (bad.Count > 0) throw new OpError("nach dem Zurückholen anders als im Zip: " + string.Join("; ", bad.ToArray()) + "." + undo);
                Log.Write(string.Format("  Zurückgeholt {0}: {1} ({2} geschrieben, {3} gleich, {4} gelöscht, geprüft){5}", g.Id, stand.Name,
                    res.Changes.Written.Count, res.Changes.Same.Count, res.Changes.Removed.Count, undo), LogKind.Info);
                return res;
            }
            catch (OpError e)
            {
                Log.Write("  Zurückholen " + g.Id + ": " + e.Message, LogKind.Error);
                throw;
            }
            finally
            {
                lock (BusyLock) if (_running == marker) _running = null;
                marker.Done = true;
                var h = Finished; if (h != null) try { h(marker); } catch { }
            }
        }

        [Op("saverestore")]
        static J SaveRestoreOp(Conn c, Req r)
        {
            try
            {
                string id = r.Str("game");
                if (string.IsNullOrEmpty(id)) return J.Err("game fehlt");
                string dest = r.Has("dest") ? r.Str("dest") : null;
                if (!r.Has("zip"))
                {
                    var stands = Stands(id, dest);
                    string install, save;
                    var g = Find(id);
                    Locate(g, out install, out save);
                    return J.Ok("game", g.Id, "title", g.Title, "dest", Dest(dest), "save_dir", save,
                        "stands", stands.ConvertAll(s => (object)s.ToJ()));
                }
                return RunRestore(id, r.Str("zip"), dest, r.Bool("dry_run")).ToJ();
            }
            catch (OpError e) { return J.Err(e.Message); }
        }
    }
}
