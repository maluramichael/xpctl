using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace XpServer
{
    public enum LogKind { Info, Op, Ok, Error, Denied, System }

    public class LogLine
    {
        public DateTime Time;
        public string Text;
        public LogKind Kind;
        public string Client;   // IP oder null
    }

    /// <summary>Log in xpserver.log (UTF-8, wie bisher) und ein Ringpuffer für das Fenster.</summary>
    public static class Log
    {
        static readonly object Lock = new object();
        static readonly List<LogLine> Ring = new List<LogLine>();
        const int RingMax = 3000;
        public static string FilePath;

        /// <summary>Wird bei jeder Zeile aufgerufen (aus beliebigem Thread).</summary>
        public static event Action<LogLine> Line;

        public static void Write(string msg, LogKind kind = LogKind.Info, string client = null)
        {
            var l = new LogLine { Time = DateTime.Now, Text = msg, Kind = kind, Client = client };
            string line = l.Time.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg;
            lock (Lock)
            {
                Ring.Add(l);
                if (Ring.Count > RingMax) Ring.RemoveRange(0, Ring.Count - RingMax);
                if (FilePath != null)
                {
                    try { File.AppendAllText(FilePath, line + "\r\n", new UTF8Encoding(false)); }
                    catch { }
                }
            }
            var h = Line;
            if (h != null) { try { h(l); } catch { } }
        }

        /// <summary>Letzte Zeilen aus xpserver.log in den Ringpuffer laden (Verlauf über Neustarts/Updates hinweg).</summary>
        public static void LoadHistory(int max)
        {
            if (FilePath == null || !File.Exists(FilePath)) return;
            try
            {
                byte[] all;
                using (var f = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long start = Math.Max(0, f.Length - 200 * 1024);
                    f.Seek(start, SeekOrigin.Begin);
                    all = new byte[f.Length - start];
                    int got = 0;
                    while (got < all.Length) { int n = f.Read(all, got, all.Length - got); if (n <= 0) break; got += n; }
                }
                var lines = Encoding.UTF8.GetString(all).Split('\n');
                var hist = new List<LogLine>();
                for (int i = Math.Max(1, lines.Length - max - 1); i < lines.Length; i++)
                {
                    string ln = lines[i].TrimEnd('\r');
                    DateTime t;
                    if (ln.Length < 21 || !DateTime.TryParseExact(ln.Substring(0, 19), "yyyy-MM-dd HH:mm:ss",
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out t)) continue;
                    string msg = ln.Substring(21);
                    hist.Add(new LogLine { Time = t, Text = msg, Kind = Guess(msg) });
                }
                lock (Lock) Ring.InsertRange(0, hist);
            }
            catch { }
        }

        static LogKind Guess(string m)
        {
            if (m.StartsWith("ABGELEHNT")) return LogKind.Denied;
            if (m.StartsWith("  Fehler:") || m.StartsWith("Verbindungsfehler") || m.StartsWith("ABSTURZ") || m.StartsWith("UI-Fehler")) return LogKind.Error;
            if (m.StartsWith("  exit=")) return LogKind.Ok;
            if (m.StartsWith(" ") || m.StartsWith("xpserver v") || m.StartsWith("beendet") || m.StartsWith("Absturz") ||
                m.StartsWith("Rollback") || m.StartsWith("Strg+C") || m.StartsWith("Neustart") || m.StartsWith("Verbindungen ")) return LogKind.System;
            return LogKind.Op;
        }

        public static LogLine[] Snapshot()
        {
            lock (Lock) return Ring.ToArray();
        }
    }
}
