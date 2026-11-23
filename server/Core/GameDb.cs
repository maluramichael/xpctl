using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace XpServer
{
    /// <summary>
    /// Versionsdatenbank der Spiele (client/game-versions.json). Beim Bauen eingebettet; liegt eine
    /// game-versions.json neben xpserver.exe und ist ihr "stand" neuer, gilt die Datei (bei gleichem Stand die eingebettete).
    /// Geladen wird einmal und erst bei Bedarf, danach nur neu, wenn sich die Datei ändert.
    /// Nachschlagen per md5 oder per Exe-Name + Größe (in der Datenbank eindeutig, siehe Test).
    /// </summary>
    public sealed class GameDb
    {
        public sealed class Entry
        {
            public string Game, Exe, Md5, Version, Latest, Status, Confidence, GogId, Probe;
            public long Size;
            public string[] Reg = new string[0];
        }

        public const string FileName = "game-versions.json";
        public readonly List<Entry> All = new List<Entry>();
        public string Stand = "", Source = "";
        public long LoadMs;
        readonly Dictionary<string, List<Entry>> _byMd5 = new Dictionary<string, List<Entry>>();
        readonly Dictionary<string, Entry> _byExeSize = new Dictionary<string, Entry>();
        readonly Dictionary<string, List<Entry>> _byGog = new Dictionary<string, List<Entry>>();

        static readonly object Lock = new object();
        static GameDb _cur;
        static DateTime _curFileTime = DateTime.MinValue;

        public static string ExternalPath { get { return Path.Combine(Program.Here, FileName); } }

        /// <summary>Aktuelle Datenbank (wirft nur, wenn weder Datei noch eingebettete Fassung lesbar ist).</summary>
        public static GameDb Get()
        {
            lock (Lock)
            {
                DateTime ft = File.Exists(ExternalPath) ? File.GetLastWriteTimeUtc(ExternalPath) : DateTime.MinValue;
                if (_cur != null && ft == _curFileTime) return _cur;
                var sw = Stopwatch.StartNew();
                GameDb emb = null, ext = null;
                string extErr = null;
                try { emb = FromText(ReadEmbedded(), "eingebettet"); } catch (Exception e) { Log.Write("  Spiele-DB eingebettet unlesbar: " + e.Message, LogKind.Error); }
                if (ft != DateTime.MinValue)
                {
                    try { ext = FromText(File.ReadAllText(ExternalPath, Encoding.UTF8), "Datei"); }
                    catch (Exception e) { extErr = e.Message; }
                }
                GameDb pick = ext != null && (emb == null || string.CompareOrdinal(ext.Stand, emb.Stand) > 0) ? ext : emb;
                if (pick == null) throw new OpError("Spiele-Datenbank nicht lesbar" + (extErr != null ? ": " + extErr : ""));
                if (extErr != null) Log.Write("  " + FileName + " neben der Exe unlesbar, nehme eingebettete: " + extErr, LogKind.Error);
                pick.LoadMs = sw.ElapsedMilliseconds;
                _cur = pick;
                _curFileTime = ft;
                Log.Write(string.Format("  Spiele-DB geladen: {0} Einträge, Stand {1} ({2}, {3} ms)", pick.All.Count, pick.Stand, pick.Source, pick.LoadMs), LogKind.System);
                return pick;
            }
        }

        static string ReadEmbedded()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(FileName))
            {
                if (s == null) throw new InvalidOperationException("Ressource fehlt");
                using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }

        static string S(J o, string k) { object v; return o.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null; }

        public static GameDb FromText(string text, string source)
        {
            var root = Json.Parse(text) as J;
            if (root == null) throw new FormatException("kein JSON-Objekt");
            var db = new GameDb { Source = source, Stand = S(root, "stand") ?? "" };
            object games;
            if (!root.TryGetValue("games", out games) || !(games is List<object>)) throw new FormatException("games fehlt");
            foreach (var o in (List<object>)games)
            {
                var j = o as J;
                if (j == null) continue;
                var e = new Entry
                {
                    Game = S(j, "game") ?? "?", Exe = S(j, "exe") ?? "", Md5 = (S(j, "md5") ?? "").ToLowerInvariant(),
                    Version = S(j, "version") ?? "", Latest = S(j, "latest") ?? "", Status = S(j, "status") ?? "unklar",
                    Confidence = S(j, "confidence"), GogId = S(j, "gog_id"), Probe = S(j, "probe"),
                };
                object sz;
                if (j.TryGetValue("size", out sz) && sz != null) e.Size = Convert.ToInt64(sz, CultureInfo.InvariantCulture);
                object reg;
                if (j.TryGetValue("reg", out reg) && reg is List<object>)
                {
                    var l = new List<string>();
                    foreach (var x in (List<object>)reg) if (x != null) l.Add(x.ToString());
                    e.Reg = l.ToArray();
                }
                db.Add(e);
            }
            return db;
        }

        void Add(Entry e)
        {
            All.Add(e);
            List<Entry> l;
            if (!_byMd5.TryGetValue(e.Md5, out l)) _byMd5[e.Md5] = l = new List<Entry>();
            l.Add(e);
            string k = Key(e.Exe, e.Size);
            if (!_byExeSize.ContainsKey(k)) _byExeSize[k] = e;
            if (e.GogId != null)
            {
                if (!_byGog.TryGetValue(e.GogId, out l)) _byGog[e.GogId] = l = new List<Entry>();
                l.Add(e);
            }
        }

        static string Key(string exe, long size) { return exe.ToLowerInvariant() + "|" + size.ToString(CultureInfo.InvariantCulture); }

        static string Norm(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in (s ?? "").ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>Aus mehreren Einträgen (gleiche Exe bei mehreren Spielen) den zum Namen passenden wählen.</summary>
        static Entry Pick(List<Entry> l, string name)
        {
            if (l == null || l.Count == 0) return null;
            if (l.Count == 1 || string.IsNullOrEmpty(name)) return l[0];
            string want = Norm(name);
            foreach (var e in l) if (Norm(e.Game) == want) return e;
            foreach (var e in l) { string g = Norm(e.Game); if (want.Length > 0 && (g.Contains(want) || want.Contains(g))) return e; }
            return l[0];
        }

        public Entry ByMd5(string md5, string name)
        {
            List<Entry> l;
            return md5 != null && _byMd5.TryGetValue(md5.ToLowerInvariant(), out l) ? Pick(l, name) : null;
        }

        public Entry ByExeSize(string exeName, long size, string name)
        {
            Entry e;
            if (!_byExeSize.TryGetValue(Key(exeName, size), out e)) return null;
            return ByMd5(e.Md5, name) ?? e;
        }

        public List<Entry> ByGog(string id)
        {
            List<Entry> l;
            return id != null && _byGog.TryGetValue(id, out l) ? l : new List<Entry>();
        }
    }
}
