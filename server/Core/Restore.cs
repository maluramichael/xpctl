using System;
using System.Collections.Generic;
using System.IO;

namespace XpServer
{
    /// <summary>
    /// Spielstand-Ordner auf den Inhalt eines Sicherungs-Zips zurücksetzen. Bewusst ohne Registry, Prozesse
    /// und Oberfläche, damit es auch außerhalb von Windows testbar ist (server/tests/restore).
    /// Zurückrollen heißt: hinterher liegen im Ordner genau die Dateien des Zips mit genau diesem Inhalt.
    /// Dateien, die es heute gibt, im Zip aber nicht (z. B. ein später angelegter Diablo-II-Charakter),
    /// werden gelöscht; der Aufrufer sichert den Ordner vorher. Leere Unterordner bleiben stehen.
    /// </summary>
    public static class Restore
    {
        public const string InfoName = "info.json";
        const string TempSuffix = ".xpctl-restore";

        public sealed class File1
        {
            public string Name;          // relativ, mit '/'
            public byte[] Data;
            public DateTime Time;
        }

        public sealed class Result
        {
            public readonly List<string> Written = new List<string>(), Same = new List<string>(), Removed = new List<string>();
        }

        /// <summary>Zip vollständig lesen und prüfen (jeder Eintrag entpackt, CRC und Größe). Liefert die
        /// Dateien ohne info.json und ohne Zwischendateien (*.xpctl-restore); info bekommt den Inhalt von
        /// info.json (null, wenn es fehlt). Wirft IOException bei kaputtem Zip oder unsicherem Namen
        /// (absolut, Laufwerk, '..').</summary>
        public static List<File1> Load(byte[] zip, out byte[] info)
        {
            info = null;
            var l = new List<File1>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in Zip.List(zip))
            {
                if (e.Name.EndsWith("/")) continue;              // Ordnereintrag (schreibt xpserver nicht, andere Packer schon)
                if (e.Name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase)) continue;   // Rest eines abgebrochenen Zurückholens, mitgesichert
                var data = Zip.Read(zip, e);
                if (e.Name == InfoName) { info = data; continue; }
                string n = SafeName(e.Name);
                if (!seen.Add(n)) throw new IOException(n + " steht doppelt im Zip");
                l.Add(new File1 { Name = n, Data = data, Time = e.Time });
            }
            return l;
        }

        /// <summary>Name aus dem Zip als sicherer relativer Pfad mit '/'.</summary>
        public static string SafeName(string n)
        {
            n = (n ?? "").Replace('\\', '/');
            if (n.Length == 0 || n.StartsWith("/") || n.IndexOf(':') >= 0) throw new IOException("unsicherer Name im Zip: " + n);
            foreach (var part in n.Split('/'))
                if (part.Length == 0 || part == "." || part == "..")
                    throw new IOException("unsicherer Name im Zip: " + n);
            return n;
        }

        static string Full(string dir, string name)
        {
            return Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>Alle Dateien unter dir, relativ mit '/', sortiert.</summary>
        public static List<string> Scan(string dir)
        {
            var l = new List<string>();
            string root = dir.TrimEnd('\\', '/');
            foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                l.Add(f.Substring(root.Length + 1).Replace('\\', '/'));
            l.Sort(StringComparer.OrdinalIgnoreCase);
            return l;
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static void Writable(string p)
        {
            var a = File.GetAttributes(p);
            if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(p, a & ~FileAttributes.ReadOnly);
        }

        /// <summary>Ordner auf die Dateien setzen. Gleiche Dateien bleiben unangetastet, andere werden über eine
        /// Zwischendatei ersetzt, überzählige gelöscht. times: Änderungszeit je Name (aus info.json, genauer als
        /// die 2-Sekunden-Zeit im Zip); fehlt ein Name, gilt die Zeit aus dem Zip. dryRun: nur einordnen.</summary>
        public static Result Apply(string dir, List<File1> files, IDictionary<string, DateTime> times, bool dryRun = false)
        {
            if (!Directory.Exists(dir)) throw new IOException("Spielstand-Ordner fehlt: " + dir);
            var res = new Result();
            var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files) want.Add(f.Name);
            var before = Scan(dir);

            foreach (var f in files)
            {
                string p = Full(dir, f.Name);
                if (File.Exists(p) && Same(File.ReadAllBytes(p), f.Data)) { res.Same.Add(f.Name); continue; }
                if (dryRun) { res.Written.Add(f.Name); continue; }
                string parent = Path.GetDirectoryName(p);
                if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
                string tmp = p + TempSuffix;
                if (File.Exists(tmp)) { Writable(tmp); File.Delete(tmp); }
                File.WriteAllBytes(tmp, f.Data);
                if (File.Exists(p)) { Writable(p); File.Delete(p); }
                File.Move(tmp, p);
                DateTime t;
                File.SetLastWriteTime(p, times != null && times.TryGetValue(f.Name, out t) ? t : f.Time);
                res.Written.Add(f.Name);
            }
            foreach (var n in before)
            {
                if (want.Contains(n)) continue;
                string p = Full(dir, n);
                if (!File.Exists(p)) continue;
                if (dryRun) { res.Removed.Add(n); continue; }
                Writable(p);
                File.Delete(p);
                res.Removed.Add(n);
            }
            return res;
        }

        /// <summary>Byte-für-Byte-Vergleich des Ordners mit den Dateien. Leere Liste = gleich.</summary>
        public static List<string> Verify(string dir, List<File1> files)
        {
            var bad = new List<string>();
            var want = new Dictionary<string, File1>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files) want[f.Name] = f;
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in Scan(dir))
            {
                have.Add(n);
                if (!want.ContainsKey(n)) bad.Add(n + ": nicht im Zip, liegt aber im Ordner");
            }
            foreach (var f in files)
            {
                if (!have.Contains(f.Name)) { bad.Add(f.Name + ": fehlt im Ordner"); continue; }
                if (!Same(File.ReadAllBytes(Full(dir, f.Name)), f.Data)) bad.Add(f.Name + ": Inhalt anders als im Zip");
            }
            return bad;
        }
    }
}
