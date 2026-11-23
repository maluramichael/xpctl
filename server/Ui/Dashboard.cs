using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace XpServer.Ui
{
    /// <summary>Hauptfenster: Status, Zahlen, Aktivität der letzten 30 Minuten, Live-Log.</summary>
    public class Dashboard : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        readonly LogView _log = new LogView();
        readonly TextBox _filter = new TextBox();
        readonly Label _hint = new Label { Text = "Filter (Strg+F)", AutoSize = false };
        readonly Chip _errors, _lock, _clear, _close, _min;
        readonly Timer _tick = new Timer { Interval = 1000 };
        readonly Activity _act;
        const int Head = 64, Cards = 76, Spark = 64, Bar = 34, Foot = 26, Pad = 14;

        public Dashboard(Activity act)
        {
            _act = act;
            Text = "xpserver";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(840, 560);
            MinimumSize = new Size(560, 380);
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui;
            ShowInTaskbar = true;
            KeyPreview = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Icon = Icon.FromHandle(IconMaker.Draw(32, TrayState.Idle, 0).GetHicon());

            _filter.BorderStyle = BorderStyle.None;
            _filter.BackColor = Theme.Panel2;
            _filter.ForeColor = Theme.Text;
            _filter.Font = Theme.Ui;
            _filter.TextChanged += (s, e) => { _log.SetFilter(_filter.Text, _errors.On); UpdateHint(); };
            _filter.GotFocus += (s, e) => { UpdateHint(); Invalidate(); };
            _filter.LostFocus += (s, e) => { UpdateHint(); Invalidate(); };
            _hint.BackColor = Theme.Panel2;
            _hint.ForeColor = Theme.Faint;
            _hint.Font = Theme.Ui;
            _hint.Click += (s, e) => _filter.Focus();

            _errors = new Chip("Nur Fehler", true);
            _errors.Click += (s, e) => { _errors.On = !_errors.On; _log.SetFilter(_filter.Text, _errors.On); };
            _lock = new Chip("Sperren", true);
            _lock.Click += (s, e) => TrayApp.SetLocked(!Server.Locked);
            _clear = new Chip("Ansicht leeren", false);
            _clear.Click += (s, e) => _log.ClearView();
            _close = new Chip("×", false) { Ghost = true };
            _close.Click += (s, e) => Hide();
            _min = new Chip("–", false) { Ghost = true };
            _min.Click += (s, e) => WindowState = FormWindowState.Minimized;

            Controls.AddRange(new Control[] { _hint, _log, _filter, _errors, _lock, _clear, _close, _min });
            _hint.BringToFront();
            _log.SetLines(Log.Snapshot());
            Log.Line += OnLine;
            _tick.Tick += (s, e) => { _lock.On = Server.Locked; Invalidate(new Rectangle(0, 0, Width, Head + Cards + Spark + 8)); };
            _tick.Start();
            Layout2();
        }

        void UpdateHint() { _hint.Visible = _filter.Text.Length == 0 && !_filter.Focused; }

        protected override bool ShowWithoutActivation { get { return !_userOpen; } }
        bool _userOpen;

        /// <summary>Fenster auf Wunsch des Benutzers zeigen (dann darf es den Fokus bekommen).</summary>
        public void ShowByUser()
        {
            _userOpen = true;
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            Activate();
        }

        void OnLine(LogLine l)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)(() => _log.Add(l))); } catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            Log.Line -= OnLine;
            base.OnFormClosing(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Hide();
            else if (e.Control && e.KeyCode == Keys.F) _filter.Focus();
            base.OnKeyDown(e);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Layout2(); }

        void Layout2()
        {
            if (_log == null || _close == null) return;
            int w = ClientSize.Width, h = ClientSize.Height;
            _close.SetBounds(w - 38, 14, 26, 24);
            _min.SetBounds(w - 66, 14, 26, 24);
            int by = Head + Cards + Spark + 10;
            _filter.SetBounds(Pad + 30, by + 9, 200, 16);
            _hint.SetBounds(Pad + 30, by + 9, 200, 16);
            _errors.SetBounds(Pad + 250, by + 4, 86, 24);
            _lock.SetBounds(w - Pad - 196, by + 4, 80, 24);
            _clear.SetBounds(w - Pad - 110, by + 4, 110, 24);
            _log.SetBounds(Pad + 1, by + Bar + 1, w - 2 * Pad - 2, h - by - Bar - Foot - 2);
            Invalidate();
        }

        // Ziehen am Kopf, Größe ändern unten rechts
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            if (e.X > ClientSize.Width - 18 && e.Y > ClientSize.Height - 18)
                SendMessage(Handle, 0xA1, (IntPtr)17, IntPtr.Zero);   // HTBOTTOMRIGHT
            else if (e.Y < Head)
                SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);    // HTCAPTION
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Cursor = e.X > ClientSize.Width - 18 && e.Y > ClientSize.Height - 18 ? Cursors.SizeNWSE : Cursors.Default;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.Smooth(g);
            int w = ClientSize.Width, h = ClientSize.Height;
            g.Clear(Theme.Bg);
            using (var pen = new Pen(Theme.Border)) g.DrawRectangle(pen, 0, 0, w - 1, h - 1);

            // Kopf mit Verlauf
            using (var br = new LinearGradientBrush(new Rectangle(0, 0, w, Head), Color.FromArgb(28, 33, 42), Theme.Bg, 90f))
                g.FillRectangle(br, 1, 1, w - 2, Head);
            bool busy = Server.ActiveOps > 0;
            var st = Server.Locked ? TrayState.Locked : busy ? TrayState.Busy : TrayState.Idle;
            using (var ico = IconMaker.Draw(36, st, (float)(DateTime.Now.Millisecond / 1000.0)))
                g.DrawImage(ico, Pad, 14);
            TextRenderer.DrawText(g, "xpserver", Theme.Title, new Point(Pad + 46, 14), Theme.Text, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "v" + Program.Version + "  ·  " + Environment.MachineName.ToLowerInvariant() + "  ·  .NET " + Environment.Version.Major + "." + Environment.Version.Minor,
                Theme.Ui, new Point(Pad + 47, 36), Theme.Dim, TextFormatFlags.NoPadding);
            string status = Server.Locked ? "GESPERRT" : busy ? "ARBEITET" : "LAUSCHT";
            Color sc = Server.Locked ? Theme.Red : busy ? Theme.Amber : Theme.Accent;
            string pillText = status + "  :" + Program.Opt.Port;
            var psz = TextRenderer.MeasureText(pillText, Theme.UiBold);
            var pill = new RectangleF(w - 86 - psz.Width - 26, 16, psz.Width + 26, 22);
            using (var p = Theme.Round(pill, 11))
            using (var b = new SolidBrush(Theme.Alpha(sc, 36))) g.FillPath(b, p);
            using (var b = new SolidBrush(sc)) g.FillEllipse(b, pill.X + 9, pill.Y + 8, 6, 6);
            TextRenderer.DrawText(g, pillText, Theme.UiBold, new Point((int)pill.X + 19, (int)pill.Y + 4), sc, TextFormatFlags.NoPadding);

            // Karten
            var up = DateTime.Now - Server.Started;
            string upText = up.TotalDays >= 1 ? string.Format("{0}d {1}h", (int)up.TotalDays, up.Hours) :
                            up.TotalHours >= 1 ? string.Format("{0}h {1:00}m", (int)up.TotalHours, up.Minutes) :
                            string.Format("{0}m {1:00}s", up.Minutes, up.Seconds);
            var nets = new List<string>();
            foreach (var n in Program.Allow) nets.Add(n.ToString());
            var cards = new[] {
                new[] { "BEFEHLE", Server.OpsTotal.ToString(), "seit Start" },
                new[] { "FEHLER", Server.ErrorsTotal.ToString(), Server.ErrorsTotal == 0 ? "alles sauber" : "siehe Log" },
                new[] { "VERBINDUNGEN", Server.ClientsNow.ToString(), Server.ClientsNow == 1 ? "Client aktiv" : "Clients aktiv" },
                new[] { "LAUFZEIT", upText, Server.Started.ToString("dd.MM. HH:mm") },
                new[] { "ERLAUBT", nets.Count > 0 ? nets[0] : "-", nets.Count > 1 ? "+" + (nets.Count - 1) + " weitere" : "Netz" },
            };
            int cw = (w - 2 * Pad - 4 * 10) / 5;
            for (int i = 0; i < cards.Length; i++)
            {
                var r = new RectangleF(Pad + i * (cw + 10), Head + 4, cw, Cards - 10);
                using (var p = Theme.Round(r, 8))
                {
                    using (var b = new SolidBrush(Theme.Panel)) g.FillPath(b, p);
                    using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, p);
                }
                Color vc = i == 1 && Server.ErrorsTotal > 0 ? Theme.Red : i == 2 && Server.ClientsNow > 0 ? Theme.Amber : Theme.Text;
                TextRenderer.DrawText(g, cards[i][0], Theme.Small, new Point((int)r.X + 12, (int)r.Y + 9), Theme.Dim, TextFormatFlags.NoPadding);
                var big = cards[i][1].Length > 10 ? Theme.UiBold : Theme.Big;
                TextRenderer.DrawText(g, cards[i][1], big, new Rectangle((int)r.X + 10, (int)r.Y + 22, (int)r.Width - 14, 26), vc,
                    TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, cards[i][2], Theme.Small, new Point((int)r.X + 12, (int)r.Y + 50), Theme.Faint, TextFormatFlags.NoPadding);
            }

            // Aktivität
            var sr = new RectangleF(Pad, Head + Cards, w - 2 * Pad, Spark);
            using (var p = Theme.Round(sr, 8))
            {
                using (var b = new SolidBrush(Theme.Panel)) g.FillPath(b, p);
                using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, p);
            }
            TextRenderer.DrawText(g, "AKTIVITÄT  ·  letzte 30 Minuten", Theme.Small, new Point(Pad + 12, (int)sr.Y + 8), Theme.Dim, TextFormatFlags.NoPadding);
            var buckets = _act.Minutes(30);
            int max = 1;
            foreach (var x in buckets) max = Math.Max(max, x);
            TextRenderer.DrawText(g, "max " + max + "/min", Theme.Small, new Rectangle(0, (int)sr.Y + 8, (int)sr.Right - 12, 12), Theme.Faint,
                TextFormatFlags.Right | TextFormatFlags.NoPadding);
            float bw = (sr.Width - 24) / buckets.Length;
            float baseY = sr.Bottom - 8, hMax = sr.Height - 30;
            for (int i = 0; i < buckets.Length; i++)
            {
                float bh = buckets[i] == 0 ? 2 : Math.Max(4, hMax * buckets[i] / max);
                var br = new RectangleF(sr.X + 12 + i * bw + 1.5f, baseY - bh, bw - 3, bh);
                Color c = buckets[i] == 0 ? Theme.Panel2 : Theme.Mix(Theme.Accent, Theme.Amber, (float)buckets[i] / max);
                using (var p = Theme.Round(br, Math.Min(2.5f, br.Width / 2)))
                using (var b = new SolidBrush(c)) g.FillPath(b, p);
            }

            // Leiste über dem Log
            int by = Head + Cards + Spark + 10;
            var fr = new RectangleF(Pad, by + 4, 236, 24);
            using (var p = Theme.Round(fr, 6))
            {
                using (var b = new SolidBrush(Theme.Panel2)) g.FillPath(b, p);
                using (var pen = new Pen(_filter.Focused ? Theme.Accent : Theme.Border)) g.DrawPath(pen, p);
            }
            using (var pen = new Pen(Theme.Dim, 1.6f))
            {
                g.DrawEllipse(pen, Pad + 9, by + 10, 9, 9);
                g.DrawLine(pen, Pad + 17, by + 18, Pad + 21, by + 22);
            }

            // Rahmen ums Log
            var lr = new RectangleF(Pad, by + Bar, w - 2 * Pad - 1, h - by - Bar - Foot);
            using (var pen = new Pen(Theme.Border)) g.DrawRectangle(pen, lr.X, lr.Y, lr.Width, lr.Height);

            // Fuß
            TextRenderer.DrawText(g, "Log: " + Log.FilePath, Theme.Small, new Rectangle(Pad, h - Foot + 7, w - 2 * Pad - 160, 14), Theme.Faint,
                TextFormatFlags.NoPadding | TextFormatFlags.PathEllipsis);
            TextRenderer.DrawText(g, "Esc schließt ins Tray", Theme.Small, new Rectangle(0, h - Foot + 7, w - Pad - 14, 14), Theme.Faint,
                TextFormatFlags.Right | TextFormatFlags.NoPadding);
            using (var pen = new Pen(Theme.Faint))
                for (int i = 0; i < 3; i++) g.DrawLine(pen, w - 4 - i * 4, h - 3, w - 3, h - 4 - i * 4);
        }

        protected override void OnActivated(EventArgs e) { base.OnActivated(e); Invalidate(); }
    }

    /// <summary>Flacher Knopf mit optionalem Ein/Aus-Zustand.</summary>
    public class Chip : Control
    {
        public bool Toggle, Ghost;
        bool _on, _hover, _down;
        public bool On { get { return _on; } set { if (_on != value) { _on = value; Invalidate(); } } }

        public Chip(string text, bool toggle)
        {
            Text = text;
            Toggle = toggle;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Theme.Bg;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            Theme.Smooth(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color acc = Text == "Sperren" ? Theme.Red : Theme.Accent;
            Color fill = Ghost ? (_hover ? Theme.Panel2 : Theme.Bg) : _on ? Theme.Alpha(acc, 48) : _hover ? Theme.Panel2 : Theme.Panel;
            if (_down) fill = Theme.Mix(fill, Color.Black, 0.25f);
            Color border = _on ? acc : Ghost ? Color.Transparent : Theme.Border;
            using (var p = Theme.Round(r, 6))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                if (border.A > 0) using (var pen = new Pen(border)) g.DrawPath(pen, p);
            }
            string t = Text == "Sperren" && _on ? "Gesperrt" : Text;
            TextRenderer.DrawText(g, t, Ghost ? Theme.Title : Theme.Ui, new Rectangle(0, Ghost ? -1 : 0, Width, Height), _on ? acc : Ghost && _hover ? Theme.Text : Theme.Mix(Theme.Text, Theme.Dim, Ghost ? 0.5f : 0f),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Befehle pro Minute (Ringpuffer).</summary>
    public class Activity
    {
        readonly int[] _b = new int[60];
        long _minute = -1;
        readonly object _lock = new object();

        static long Now() { return DateTime.Now.Ticks / TimeSpan.TicksPerMinute; }

        void Roll(long now)
        {
            if (_minute < 0) { _minute = now; return; }
            for (long m = _minute + 1; m <= now && m <= _minute + 60; m++) _b[m % 60] = 0;
            if (now > _minute) _minute = now;
        }

        public void Hit()
        {
            lock (_lock) { long n = Now(); Roll(n); _b[n % 60]++; }
        }

        public int[] Minutes(int count)
        {
            lock (_lock)
            {
                long n = Now();
                Roll(n);
                var r = new int[count];
                for (int i = 0; i < count; i++) r[i] = _b[(n - (count - 1 - i) + 600) % 60];
                return r;
            }
        }
    }
}
