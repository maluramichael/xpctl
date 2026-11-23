using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace XpServer.Ui
{
    /// <summary>Symbol im Infobereich: Zustand (bereit, arbeitet, Fehler, gesperrt), Menü, Live-Fenster.
    /// Nimmt nie selbst den Fokus und zeigt keine Hinweise, solange etwas im Vollbild läuft.</summary>
    public class TrayApp : ApplicationContext
    {
        static TrayApp _inst;
        readonly NotifyIcon _icon = new NotifyIcon();
        readonly Timer _anim = new Timer { Interval = 90 };
        readonly Activity _act = new Activity();
        readonly ToolStripMenuItem _head, _locked, _notify, _autostart;
        Dashboard _dash;
        IntPtr _hicon;
        float _phase;
        DateTime _errorUntil = DateTime.MinValue;
        TrayState _shown = (TrayState)(-1);
        string _lastTip;
        readonly System.Threading.SynchronizationContext _ui;

        public TrayApp()
        {
            _inst = this;
            _ui = System.Threading.SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

            var m = new ContextMenuStrip { Renderer = new DarkMenu(), ShowImageMargin = true, Font = Theme.Ui };
            m.BackColor = Theme.Panel;
            _head = new ToolStripMenuItem("xpserver") { Enabled = false, Tag = "head", Font = Theme.UiBold };
            m.Items.Add(_head);
            m.Items.Add(new ToolStripSeparator());
            var open = new ToolStripMenuItem("Live-Fenster öffnen", null, (s, e) => ShowDash()) { Font = Theme.UiBold };
            m.Items.Add(open);
            _locked = new ToolStripMenuItem("Verbindungen sperren", null, (s, e) => SetLocked(!Server.Locked));
            m.Items.Add(_locked);
            _notify = new ToolStripMenuItem("Hinweis bei jedem Befehl", null, (s, e) =>
            {
                Desk.SetFlag("notify", !Desk.GetFlag("notify", false));
            });
            m.Items.Add(_notify);
            m.Items.Add(new ToolStripSeparator());
            var more = new ToolStripMenuItem("Einrichtung");
            more.DropDown.Renderer = new DarkMenu();
            more.DropDown.BackColor = Theme.Panel;
            _autostart = new ToolStripMenuItem("Mit Windows starten", null, (s, e) =>
            {
                try { Desk.Autostart = !Desk.Autostart; }
                catch (Exception ex) { Dialogs.Error("Autostart nicht geändert: " + ex.Message); }
            });
            more.DropDownItems.Add(_autostart);
            more.DropDownItems.Add("Firewall-Freigabe einrichten", null, (s, e) =>
                Dialogs.Info("Firewall", Desk.AllowFirewall()));
            more.DropDownItems.Add("Konfiguration öffnen (xpserver.json)", null, (s, e) => Desk.Open(EnsureConfig()));
            more.DropDownItems.Add("Log-Datei öffnen", null, (s, e) => Desk.Open(Log.FilePath));
            more.DropDownItems.Add("Ordner öffnen", null, (s, e) => Desk.Open(Program.Here));
            m.Items.Add(more);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Neu starten", null, (s, e) => Supervisor.Restart());
            m.Items.Add("Beenden", null, (s, e) =>
            {
                if (Dialogs.Confirm("xpserver beenden?", "Danach ist der Rechner nicht mehr per xpctl erreichbar."))
                    Quit(0);
            });
            Space(m);
            Space(more.DropDown);
            m.Opening += (s, e) =>
            {
                _head.Text = "xpserver v" + Program.Version + "  ·  Port " + Program.Opt.Port;
                _locked.Checked = Server.Locked;
                _notify.Checked = Desk.GetFlag("notify", false);
                _autostart.Checked = Desk.Autostart;
            };

            _icon.ContextMenuStrip = m;
            _icon.DoubleClick += (s, e) => ShowDash();
            _icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowDash(); };
            Server.OpStarted += (ip, desc, x) =>
            {
                _act.Hit();
                if (Desk.GetFlag("notify", false)) Post(() => Balloon("Befehl von " + ip, desc, ToolTipIcon.None));
            };
            Server.OpFinished += (ip, op, ok) => { if (!ok) _errorUntil = DateTime.Now.AddSeconds(8); };
            _anim.Tick += (s, e) => Animate();
            Render(TrayState.Idle);
            _icon.Visible = true;
            _anim.Start();

            if (!Program.Opt.AfterUpdate && Program.Opt.CrashCount == 0 && !Desk.GetFlag("welcomed", false))
            {
                Desk.SetFlag("welcomed", true);
                Balloon("xpserver läuft", "Port " + Program.Opt.Port + ". Klick auf das Symbol zeigt das Live-Fenster.", ToolTipIcon.Info);
            }
        }

        void Post(Action a) { _ui.Post(_ => a(), null); }

        /// <summary>Hinweisblase, aber nie über einem Vollbildprogramm (Spiel).</summary>
        void Balloon(string title, string text, ToolTipIcon ic)
        {
            if (Desk.FullscreenActive()) return;
            _icon.ShowBalloonTip(4000, title, text.Length > 200 ? text.Substring(0, 200) + " ..." : text, ic);
        }

        void ShowDash()
        {
            if (_dash == null || _dash.IsDisposed) _dash = new Dashboard(_act);
            _dash.ShowByUser();
        }

        void Animate()
        {
            TrayState st = Server.Locked ? TrayState.Locked : Server.ActiveOps > 0 ? TrayState.Busy :
                           DateTime.Now < _errorUntil ? TrayState.Error : TrayState.Idle;
            if (st == TrayState.Busy)
            {
                _phase = (_phase + 0.09f) % 1f;
                Render(st);
            }
            else if (st != _shown) Render(st);
            string tip = string.Format("xpserver :{0}  ·  {1}\n{2} Befehle, {3} Fehler", Program.Opt.Port,
                st == TrayState.Locked ? "gesperrt" : st == TrayState.Busy ? "arbeitet" : "bereit", Server.OpsTotal, Server.ErrorsTotal);
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            if (tip != _lastTip) { _icon.Text = tip; _lastTip = tip; }
        }

        void Render(TrayState st)
        {
            _shown = st;
            using (var bmp = IconMaker.Draw(16, st, _phase))
                _icon.Icon = IconMaker.ToIcon(bmp, ref _hicon);
        }

        string EnsureConfig()
        {
            string p = Cfg.PathUsed ?? Path.Combine(Program.Here, Cfg.Name);
            if (!File.Exists(p))
                File.WriteAllText(p, Setup.ConfigJson(Program.Allow.ConvertAll(c => c.ToString())), new System.Text.UTF8Encoding(false));
            return p;
        }

        public static void SetLocked(bool on)
        {
            Server.Locked = on;
            Log.Write(on ? "Verbindungen gesperrt (über das Symbol)" : "Verbindungen wieder erlaubt", LogKind.System);
        }

        /// <summary>Beendet die Anwendung mit Exit-Code (aus beliebigem Thread).</summary>
        public static void Quit(int code)
        {
            try { if (_inst != null) _inst._icon.Visible = false; } catch { }
            try { if (code == 0) Log.Write("beendet.", LogKind.System); } catch { }
            Environment.Exit(code);
        }

        /// <summary>Einheitliche Abstände im Menü: Luft oben/unten, etwas höhere Zeilen.</summary>
        static void Space(ToolStripDropDown d)
        {
            d.Padding = new Padding(0, 4, 0, 4);
            foreach (ToolStripItem it in d.Items)
                if (!(it is ToolStripSeparator)) it.Padding = new Padding(0, 3, 0, 3);
        }
    }
}
