using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace XpServer.Ui
{
    /// <summary>Dunkles Dialogfenster ohne Windows-Rahmen.</summary>
    public class DarkDialog : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        public string Heading, Sub;
        public Color HeadColor = Theme.Accent;
        public const int Top0 = 70;

        public DarkDialog(string heading, string sub, int w, int h)
        {
            Heading = heading; Sub = sub;
            Text = "xpserver";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(w, h);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui;
            DoubleBuffered = true;
            KeyPreview = true;
            ShowInTaskbar = true;
            TopMost = true;
            Icon = Icon.FromHandle(IconMaker.Draw(32, TrayState.Idle, 0).GetHicon());
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            base.OnKeyDown(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && e.Y < Top0) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Theme.Smooth(g);
            g.Clear(Theme.Bg);
            using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, Width, Top0), Color.FromArgb(28, 33, 42), Theme.Bg, 90f))
                g.FillRectangle(br, 0, 0, Width, Top0);
            using (var pen = new Pen(Theme.Border)) g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            using (var pen = new Pen(HeadColor, 2)) g.DrawLine(pen, 1, 1, ClientSize.Width - 2, 1);
            using (var ico = IconMaker.Draw(32, HeadColor == Theme.Red ? TrayState.Error : TrayState.Idle, 0)) g.DrawImage(ico, 18, 18);
            TextRenderer.DrawText(g, Heading, Theme.Title, new Point(60, 17), Theme.Text, TextFormatFlags.NoPadding);
            if (Sub != null)
                TextRenderer.DrawText(g, Sub, Theme.Ui, new Point(61, 38), Theme.Dim, TextFormatFlags.NoPadding);
        }

        /// <summary>Text mit Umbruch ab y, liefert die Höhe.</summary>
        public Label AddText(string text, int y, int h, Color c)
        {
            var l = new Label { Text = text, ForeColor = c, BackColor = Theme.Bg, Font = Theme.Ui, AutoSize = false };
            l.SetBounds(20, y, ClientSize.Width - 40, h);
            Controls.Add(l);
            return l;
        }

        public Field AddField(string label, int y, bool password)
        {
            var f = new Field(label, password);
            f.SetBounds(20, y, ClientSize.Width - 40, 46);
            Controls.Add(f);
            return f;
        }

        public Check AddCheck(string text, string hint, int y, bool on)
        {
            var c = new Check(text, hint) { On = on };
            c.SetBounds(20, y, ClientSize.Width - 40, hint == null ? 22 : 36);
            Controls.Add(c);
            return c;
        }

        public Chip AddButton(string text, int right, bool primary, DialogResult res)
        {
            var b = new Chip(text, primary) { On = primary };
            int w = Math.Max(90, TextRenderer.MeasureText(text, Theme.Ui).Width + 30);
            b.SetBounds(ClientSize.Width - right - w, ClientSize.Height - 46, w, 28);
            b.Click += (s, e) => { DialogResult = res; if (res != DialogResult.None) Close(); };
            Controls.Add(b);
            return b;
        }
    }

    /// <summary>Beschriftetes Eingabefeld im dunklen Stil.</summary>
    public class Field : Control
    {
        public readonly TextBox Box = new TextBox();
        public Field(string label, bool password)
        {
            Text = label;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Theme.Bg;
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Theme.Panel2;
            Box.ForeColor = Theme.Text;
            Box.Font = new Font("Tahoma", 9.75f);
            Box.UseSystemPasswordChar = password;
            Box.GotFocus += (s, e) => Invalidate();
            Box.LostFocus += (s, e) => Invalidate();
            Controls.Add(Box);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Box.SetBounds(10, 24, Width - 20, 18); }

        // TAB landet auf dem Rahmen: Fokus ans Textfeld weitergeben, sonst geht die Eingabe ins Leere.
        protected override void OnEnter(EventArgs e) { base.OnEnter(e); Box.Focus(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            Theme.Smooth(g);
            TextRenderer.DrawText(g, Text, Theme.Small, new Point(1, 1), Theme.Dim, TextFormatFlags.NoPadding);
            using (var p = Theme.Round(new RectangleF(0.5f, 16.5f, Width - 1.5f, 28), 6))
            {
                using (var b = new SolidBrush(Theme.Panel2)) g.FillPath(b, p);
                using (var pen = new Pen(Box.Focused ? Theme.Accent : Theme.Border)) g.DrawPath(pen, p);
            }
        }
    }

    /// <summary>Schalter mit Text und optionalem Hinweis darunter.</summary>
    public class Check : Control
    {
        public string Hint;
        bool _on;
        public bool On { get { return _on; } set { _on = value; Invalidate(); } }
        public Check(string text, string hint)
        {
            Text = text; Hint = hint;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Theme.Bg;
            Cursor = Cursors.Hand;
            TabStop = false;   // TAB springt von Feld zu Feld, die Schalter sind per Maus
        }
        protected override void OnClick(EventArgs e) { On = !On; base.OnClick(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Bg);
            Theme.Smooth(g);
            var track = new RectangleF(0.5f, 2.5f, 30, 16);
            using (var p = Theme.Round(track, 8))
            using (var b = new SolidBrush(_on ? Theme.Accent : Theme.Panel2)) g.FillPath(b, p);
            using (var b = new SolidBrush(_on ? Theme.Bg : Theme.Dim)) g.FillEllipse(b, _on ? 17 : 3, 5, 11, 11);
            TextRenderer.DrawText(g, Text, Theme.Ui, new Point(40, 4), Theme.Text, TextFormatFlags.NoPadding);
            if (Hint != null) TextRenderer.DrawText(g, Hint, Theme.Small, new Point(40, 20), Theme.Faint, TextFormatFlags.NoPadding);
        }
    }

    public static class Dialogs
    {
        public static void Error(string msg) { Show("Achtung", msg, Theme.Red); }
        public static void Info(string title, string msg) { Show(title, msg, Theme.Accent); }

        static void Show(string title, string msg, Color c)
        {
            int lines = Math.Max(2, msg.Split('\n').Length + msg.Length / 70);
            using (var d = new DarkDialog(title, null, 460, DarkDialog.Top0 + lines * 15 + 70) { HeadColor = c })
            {
                d.AddText(msg, DarkDialog.Top0, lines * 15 + 4, Theme.Text);
                var ok = d.AddButton("OK", 18, true, DialogResult.OK);
                d.AcceptButton = null;
                d.Shown += (s, e) => ok.Focus();
                d.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) d.Close(); };
                d.ShowDialog();
            }
        }

        public static bool Confirm(string title, string msg)
        {
            using (var d = new DarkDialog(title, null, 420, 160))
            {
                d.AddText(msg, DarkDialog.Top0, 34, Theme.Text);
                d.AddButton("Ja", 18, true, DialogResult.Yes);
                d.AddButton("Abbrechen", 126, false, DialogResult.No);
                return d.ShowDialog() == DialogResult.Yes;
            }
        }

        public static string AskPassword(out bool remember)
        {
            remember = false;
            using (var d = new DarkDialog("Passwort", "Für die Anmeldung von xpctl", 400, 220))
            {
                var f = d.AddField("PASSWORT", DarkDialog.Top0 + 2, true);
                var c = d.AddCheck("Merken (xpctl.key neben xpserver.exe)", null, DarkDialog.Top0 + 60, true);
                d.AddButton("Starten", 18, true, DialogResult.OK);
                d.AddButton("Abbrechen", 128, false, DialogResult.Cancel);
                d.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { d.DialogResult = DialogResult.OK; d.Close(); } };
                d.Shown += (s, e) => f.Box.Focus();
                if (d.ShowDialog() != DialogResult.OK) return null;
                remember = c.On;
                return f.Box.Text;
            }
        }
    }

    /// <summary>Ersteinrichtung: Passwort, erlaubtes Netz, Autostart, Firewall. Schreibt xpserver.json und xpctl.key.</summary>
    public static class Setup
    {
        public static string ConfigJson(List<string> allow)
        {
            var sb = new StringBuilder();
            sb.Append("{\r\n  \"allow\": [");
            for (int i = 0; i < allow.Count; i++) sb.Append(i > 0 ? ", " : "").Append(Json.Dump(allow[i]));
            sb.Append("]\r\n}\r\n");
            return sb.ToString();
        }

        /// <summary>Netz des Rechners als /24 (z. B. 192.168.1.0/24).</summary>
        public static string GuessNet()
        {
            try
            {
                foreach (var a in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
                {
                    if (a.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = a.GetAddressBytes();
                    if (b[0] == 127 || (b[0] == 169 && b[1] == 254)) continue;
                    return b[0] + "." + b[1] + "." + b[2] + ".0/24";
                }
            }
            catch { }
            return "192.168.1.0/24";
        }

        public static string MyIp()
        {
            try
            {
                foreach (var a in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
                    if (a.AddressFamily == AddressFamily.InterNetwork && a.GetAddressBytes()[0] != 127) return a.ToString();
            }
            catch { }
            return "?";
        }

        /// <summary>Zeigt den Assistenten. Liefert false bei Abbruch.</summary>
        public static bool Run(string cfgPath, string keyPath, bool needKey)
        {
            using (var d = new DarkDialog("Willkommen bei xpserver", "Einmal einrichten, danach läuft alles von selbst.", 480, needKey ? 420 : 360))
            {
                int y = DarkDialog.Top0;
                d.AddText("Dieser Rechner (" + Program.Show(MyIp()) + ") wird damit per xpctl fernsteuerbar. Nur Rechner aus dem erlaubten Netz " +
                          "mit dem richtigen Passwort kommen rein.", y, 32, Theme.Dim);
                y += 40;
                Field pw = null, pw2 = null;
                if (needKey)
                {
                    pw = d.AddField("PASSWORT (gleiches Passwort im Client)", y, true); y += 52;
                    pw2 = d.AddField("PASSWORT WIEDERHOLEN", y, true); y += 56;
                }
                var net = d.AddField("ERLAUBTES NETZ (CIDR, mehrere mit Komma)", y, false); y += 56;
                net.Box.Text = Program.Show(GuessNet());
                var auto = d.AddCheck("Mit Windows starten", "Eintrag unter HKCU\\...\\Run für diesen Benutzer", y, true); y += 42;
                var fw = d.AddCheck("Firewall-Freigabe einrichten", "netsh firewall: xpserver.exe im lokalen Subnetz erlauben", y, true);
                var err = d.AddText("", d.ClientSize.Height - 72, 16, Theme.Red);
                var ok = d.AddButton("Einrichten und starten", 18, true, DialogResult.None);
                d.AddButton("Abbrechen", 196, false, DialogResult.Cancel);
                ok.Click += (s, e) =>
                {
                    if (needKey && pw.Box.Text.Trim().Length < 4) { err.Text = "Passwort: mindestens 4 Zeichen."; return; }
                    if (needKey && pw.Box.Text != pw2.Box.Text) { err.Text = "Die Passwörter sind verschieden."; return; }
                    var nets = new List<string>();
                    foreach (var p in net.Box.Text.Split(','))
                    {
                        if (p.Trim().Length == 0) continue;
                        try { new Cidr(p); nets.Add(p.Trim()); }
                        catch { err.Text = "Ungültiges Netz: " + p.Trim(); return; }
                    }
                    if (nets.Count == 0) { err.Text = "Mindestens ein Netz angeben."; return; }
                    try
                    {
                        if (File.Exists(cfgPath))
                        {
                            var o = Json.Parse(File.ReadAllText(cfgPath, Encoding.UTF8)) as J ?? new J();
                            o["allow"] = nets.ConvertAll(x => (object)x);
                            File.WriteAllText(cfgPath, Json.Dump(o), new UTF8Encoding(false));
                        }
                        else File.WriteAllText(cfgPath, ConfigJson(nets), new UTF8Encoding(false));
                        if (needKey) File.WriteAllBytes(keyPath, Encoding.UTF8.GetBytes(pw.Box.Text.Trim()));
                        if (auto.On) Desk.Autostart = true;
                        if (fw.On) Log.Write("Firewall: " + Desk.AllowFirewall().Replace("\r", " ").Replace("\n", " "), LogKind.System);
                    }
                    catch (Exception ex) { err.Text = ex.Message; return; }
                    d.DialogResult = DialogResult.OK;
                    d.Close();
                };
                d.Shown += (s, e) => { if (pw != null) pw.Box.Focus(); };
                return d.ShowDialog() == DialogResult.OK;
            }
        }
    }
}
