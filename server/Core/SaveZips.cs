using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace XpServer
{
    /// <summary>
    /// Namen der Sicherungs-Zips (<c>&lt;id&gt;_JJJJ-MM-TT_hh-mm-ss.zip</c>) und die Aufräumregel: pro Spiel bleiben
    /// die neuesten <see cref="Keep"/> Zips, ältere löscht die Sicherung nach einem neuen Zip (Entscheidung
    /// Michael 2026-11-28: 50 pro Spiel, kein Zeitplan). Nur Dateinamen, kein Dateisystem, damit es außerhalb
    /// von Windows testbar ist (server/tests/restore). Fremde Dateien und .part zählen nie mit.
    /// </summary>
    public static class SaveZips
    {
        public const int Keep = 50;

        static readonly Regex Stamp = new Regex(@"^(.+)_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}\.zip$", RegexOptions.IgnoreCase);

        /// <summary>Gehört der Dateiname (ohne Ordner) zu einer Sicherung dieses Spiels?</summary>
        public static bool Is(string name, string id)
        {
            var m = Stamp.Match(name ?? "");
            return m.Success && string.Equals(m.Groups[1].Value, id, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Sicherungen des Spiels aus einer Liste von Dateinamen, älteste zuerst (Name = Zeit).</summary>
        public static List<string> Of(IEnumerable<string> names, string id)
        {
            var l = new List<string>();
            foreach (var n in names) if (Is(n, id)) l.Add(n);
            l.Sort((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
            return l;
        }

        /// <summary>Was weg darf: alles außer den neuesten keep Sicherungen, älteste zuerst.</summary>
        public static List<string> ToPrune(IEnumerable<string> names, string id, int keep)
        {
            var all = Of(names, id);
            if (keep < 1) keep = 1;
            return all.Count <= keep ? new List<string>() : all.GetRange(0, all.Count - keep);
        }
    }
}
