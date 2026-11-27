using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using XpServer.Ops;

namespace XpServer.Ui
{
    /// <summary>
    /// Fortschritt einer Spielstand-Sicherung, nicht modal. Zeigt nur an, was <see cref="SaveBackup.Job"/>
    /// meldet; Schließen bricht die Sicherung nicht ab. Per Befehl gestartet: nimmt keinen Fokus, liegt
    /// nicht über anderen Fenstern und schließt sich nach einer erfolgreichen Sicherung von selbst.
    /// </summary>
    public class BackupDialog : DarkDialog
    {
        readonly SaveBackup.Job _job;
        readonly bool _remote;
        readonly Chip _close, _open;
        readonly Timer _auto = new Timer { Interval = 1000 };
        int _left = -1;
        const int RowH = 38;

        public BackupDialog(SaveBackup.Job job, bool remote)
            : base("Spielstände sichern", Program.Show(job.Dest), 520, Top0 + Math.Max(1, job.Games.Count) * RowH + 74)
        {
            _job = job;
            _remote = remote;
            if (remote)
            {
                TopMost = false;
                StartPosition = FormStartPosition.Manual;
                var wa = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(wa.Right - Width - 14, wa.Bottom - Height - 14);
            }
            _close = AddButton("Schließen", 18, true, DialogResult.None);
            _close.Click += (s, e) => Close();
            _open = AddButton("Zielordner öffnen", 126, false, DialogResult.None);
            _open.Click += (s, e) => Desk.Open(_job.Dest);
            _open.Visible = false;
            _auto.Tick += (s, e) =>
            {
                if (_left <= 0) return;
                _left--;
                if (_left == 0) Close(); else Invalidate();
            };
            job.Changed += OnChanged;
            FormClosed += (s, e) => { job.Changed -= OnChanged; _auto.Dispose(); };
        }

        protected override bool ShowWithoutActivation { get { return _remote; } }

        // Die Sicherung kann schon weiter (oder fertig) sein, bevor das Fenster steht.
        protected override void OnShown(EventArgs e) { base.OnShown(e); Refresh2(); }

        void OnChanged(SaveBackup.Job j)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)Refresh2); } catch { }
        }

        void Refresh2()
        {
            if (_job.Done)
            {
                bool bad = _job.Games.Exists(g => g.Status == "fehler");
                _open.Visible = true;
                HeadColor = bad ? Theme.Red : Theme.Accent;
                if (_remote && !bad && _left < 0) { _left = 8; _auto.Start(); }
            }
            Invalidate();
        }

        static Color Tone(string st)
        {
            switch (st)
            {
                case "neu": return Theme.Accent;
                case "unverändert": return Theme.Blue;
                case "fehler": return Theme.Red;
                case "sichert": return Theme.Amber;
                default: return Theme.Faint;
            }
        }

        static string Detail(SaveBackup.GameResult g)
        {
            switch (g.Status)
            {
                case "neu": return Path.GetFileName(g.Zip) + "  ·  " + g.Files + " Dateien, " + Kb(g.ZipSize) +
                                   (g.Running.Count > 0 ? "  ·  Achtung: " + string.Join(", ", g.Running.ToArray()) + " lief" : "");
                case "unverändert": return "nichts geändert seit " + Path.GetFileName(g.Zip);
                case "fehler": return g.Error;
                case "leer": return g.Error ?? "keine Dateien, kein Zip";
                case "sichert": return "liest und packt ...";
                default: return g.Status;
            }
        }

        static string Kb(long n) { return n < 1024 ? n + " B" : (n / 1024.0).ToString("0.0") + " KB"; }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            int y = Top0 + 4;
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            foreach (var r in _job.Games)
            {
                Color c = Tone(r.Status);
                using (var b = new SolidBrush(c)) g.FillEllipse(b, 22, y + 5, 8, 8);
                TextRenderer.DrawText(g, r.Title, Theme.UiBold, new Rectangle(40, y, 300, 16), Theme.Text, flags);
                TextRenderer.DrawText(g, r.Status, Theme.Ui, new Rectangle(ClientSize.Width - 160, y, 140, 16), c, flags | TextFormatFlags.Right);
                TextRenderer.DrawText(g, Detail(r), Theme.Small, new Rectangle(40, y + 17, ClientSize.Width - 60, 14),
                    r.Status == "fehler" ? Theme.Red : Theme.Dim, flags);
                y += RowH;
            }
            string foot = !_job.Done ? "läuft ..." :
                _job.Games.Exists(x => x.Status == "fehler") ? "fertig, mit Fehlern" :
                _left > 0 ? "fertig, schließt in " + _left + " s" : "fertig";
            TextRenderer.DrawText(g, foot, Theme.Small, new Point(20, ClientSize.Height - 38), Theme.Faint, TextFormatFlags.NoPadding);
        }
    }
}
