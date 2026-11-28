using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace XpServer.Ui
{
    /// <summary>Hauptfenster mit drei Reitern: Übersicht (Status, Zahlen, Aktivität, Live-Log),
    /// Spiele (bekannte Spiele, installierte und neueste Version, siehe <see cref="GameScan"/>) und
    /// Sicherung (Zielordner und Knopf für die Spielstand-Sicherung, siehe <see cref="Ops.SaveBackup"/>).</summary>
    public class Dashboard : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        readonly LogView _log = new LogView();
        readonly TextBox _filter = new TextBox();
        readonly Label _hint = new Label { Text = "Filter (Strg+F)", AutoSize = false };
        readonly Chip _errors, _lock, _clear, _close, _min, _tabMain, _tabGames, _tabSave, _rescan, _pick, _go, _back;
        readonly Field _dest = new Field("ZIELORDNER (Laufwerk oder Freigabe, muss es geben)", false);
        List<Ops.SaveBackup.GameResult> _saveRows;
        string _saveState = "";                     // Zustand des Zielordners, im Hintergrund geprüft
        Color _saveTone = Theme.Faint;
        volatile bool _saveLoading;
        readonly GamesView _games = new GamesView();
        int _tab;                                   // 0 Übersicht, 1 Spiele, 2 Sicherung
        readonly string[] _filters = { "", "", "" };    // Filtertext je Reiter
        volatile bool _scanning;
        GameScanResult _scan;
        const int TabW = 92;
        readonly Timer _tick = new Timer { Interval = 1000 };
        readonly Activity _act;
        // Raster: Außenrand Pad, zwischen allen Blöcken Gap, Innenabstand in Flächen Inner.
        const int Pad = 14, Gap = 10, Inner = 12, Head = 64, CardH = 66, SparkH = 60, BarH = 24, Foot = 30, Btn = 26;
        static int CardsY { get { return Head; } }
        static int SparkY { get { return CardsY + CardH + Gap; } }
        static int BarY { get { return SparkY + SparkH + Gap; } }
        static int LogY { get { return BarY + BarH + Gap; } }
        static int SaveGoY { get { return CardsY + 46 + 28 + Gap; } }     // unter Feld und Zustandszeile
        const int BackW = 130;                                             // Knopf Zurückholen
        static int SaveListY { get { return SaveGoY + 44 + Gap + 4; } }
        const int FilterW = 236, LockW = 80, ClearW = 110, ErrW = 86, LogInset = 4;

        public Dashboard(Activity act)
        {
            _act = act;
            Text = "xpserver";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(840, 560);
            MinimumSize = new Size(640, 380);
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - Pad, wa.Bottom - Height - Pad);
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
            _filter.TextChanged += (s, e) =>
            {
                if (_tab == 1) _games.SetFilter(_filter.Text); else _log.SetFilter(_filter.Text, _errors.On);
                UpdateHint();
            };
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

            _tabMain = new Chip("Übersicht", true) { On = true };
            _tabMain.Click += (s, e) => SetTab(0);
            _tabGames = new Chip("Spiele", true);
            _tabGames.Click += (s, e) => SetTab(1);
            _rescan = new Chip("Neu prüfen", false) { Visible = false };
            _rescan.Click += (s, e) => StartScan();
            _games.Visible = false;
            _tabSave = new Chip("Sicherung", true);
            _tabSave.Click += (s, e) => SetTab(2);
            _pick = new Chip("Wählen ...", false) { Visible = false };
            _pick.Click += (s, e) => PickTarget();
            _go = new Chip("Spielstände sichern", true) { On = true, Visible = false, Face = Theme.Title };
            _go.Click += (s, e) => StartBackup();
            _back = new Chip("Zurückholen ...", false) { Visible = false };
            _back.Click += (s, e) => { SaveTarget(); using (var d = new RestoreDialog()) d.ShowDialog(this); LoadSave(); };
            _dest.Visible = false;
            _dest.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SaveTarget(); } };
            _dest.Box.Leave += (s, e) => SaveTarget();
            Ops.SaveBackup.Finished += OnBackupFinished;

            Controls.AddRange(new Control[] { _hint, _log, _games, _filter, _errors, _lock, _clear, _close, _min, _tabMain, _tabGames, _tabSave, _rescan, _dest, _pick, _go, _back });
            _hint.BringToFront();
            _log.SetLines(Log.Snapshot());
            Log.Line += OnLine;
            _tick.Tick += (s, e) => { _lock.On = Server.Locked; Invalidate(new Rectangle(0, 0, Width, BarY)); };
            _tick.Start();
            Layout2();
        }

        void UpdateHint() { _hint.Visible = _tab != 2 && _filter.Text.Length == 0 && !_filter.Focused; }

        string SubTitle()
        {
            return "v" + Program.Version + "  ·  " + Program.Show(Environment.MachineName).ToLowerInvariant() + "  ·  .NET " +
                Environment.Version.Major + "." + Environment.Version.Minor;
        }

        void SetTab(int t)
        {
            _tabMain.On = t == 0;
            _tabGames.On = t == 1;
            _tabSave.On = t == 2;
            if (t == _tab) return;
            _filters[_tab] = _filter.Text;
            _tab = t;
            _log.Visible = _errors.Visible = _lock.Visible = _clear.Visible = t == 0;
            _games.Visible = _rescan.Visible = t == 1;
            _dest.Visible = _pick.Visible = _go.Visible = _back.Visible = t == 2;
            _filter.Visible = t != 2;
            _filter.Text = _filters[t];
            UpdateHint();
            Layout2();
            if (t == 1 && _scan == null) StartScan();
            if (t == 2) { _dest.Box.Text = Ops.SaveBackup.Target ?? ""; LoadSave(); }
        }

        // ---------- Sicherung ----------

        void OnBackupFinished(Ops.SaveBackup.Job j) { OnUi(LoadSave); }

        /// <summary>Spiele, Spielstand-Ordner, letztes Zip und Zustand des Zielordners im Hintergrund lesen
        /// (Directory.Exists kann bei getrennten Netzlaufwerken lange hängen, nie auf dem UI-Thread).</summary>
        void LoadSave()
        {
            if (_saveLoading) return;
            _saveLoading = true;
            _saveState = "prüfe Zielordner ...";
            _saveTone = Theme.Faint;
            Invalidate();
            new System.Threading.Thread(() =>
            {
                string state; Color tone; List<Ops.SaveBackup.GameResult> rows = null;
                try
                {
                    string t = Ops.SaveBackup.Target;
                    if (t == null) { state = "Noch kein Zielordner eingestellt."; tone = Theme.Amber; }
                    else if (System.IO.Directory.Exists(t)) { state = "Ordner vorhanden. Pro Spiel entsteht darin ein Unterordner."; tone = Theme.Accent; }
                    else { state = "Gibt es gerade nicht (Laufwerk verbunden, Freigabe erreichbar?). Die Sicherung bricht dann ab."; tone = Theme.Red; }
                    rows = Ops.SaveBackup.Run(null, null, true, false, false).Games;
                }
                catch (Exception ex) { state = "Fehler: " + ex.Message; tone = Theme.Red; }
                OnUi(() => { _saveState = state; _saveTone = tone; if (rows != null) _saveRows = rows; _saveLoading = false; Invalidate(); });
            }) { IsBackground = true, Name = "sicherung-übersicht" }.Start();
        }

        void SaveTarget()
        {
            string want = _dest.Box.Text.Trim();
            if (want == (Ops.SaveBackup.Target ?? "")) return;
            try { Ops.SaveBackup.Target = Ops.SaveBackup.CleanTarget(want); }
            catch (Exception ex) { Dialogs.Error(ex.Message); _dest.Box.Text = Ops.SaveBackup.Target ?? ""; return; }
            _dest.Box.Text = Ops.SaveBackup.Target ?? "";
            Log.Write("  Zielordner der Sicherung: " + (Ops.SaveBackup.Target ?? "(keiner)"), LogKind.System);
            LoadSave();
        }

        void PickTarget()
        {
            using (var d = new FolderBrowserDialog { Description = "Zielordner für die Spielstand-Sicherung", ShowNewFolderButton = true })
            {
                var cur = Ops.SaveBackup.Target;
                if (cur != null && cur.Length >= 2 && cur[1] == ':') d.SelectedPath = cur;   // kein Netzpfad: der würde hier hängen
                if (d.ShowDialog(this) != DialogResult.OK) return;
                _dest.Box.Text = d.SelectedPath;
                SaveTarget();
            }
        }

        void StartBackup()
        {
            SaveTarget();
            new System.Threading.Thread(() =>
            {
                try { Ops.SaveBackup.Run(null, null, false, false, true); }
                catch (OpError ex) { OnUi(() => Dialogs.Error(ex.Message)); }
                catch (Exception ex) { OnUi(() => Dialogs.Error("Sicherung fehlgeschlagen: " + ex.Message)); }
            }) { IsBackground = true, Name = "sicherung" }.Start();
        }

        /// <summary>Spiele suchen und im Hintergrund prüfen (niedrigste Priorität, pausiert bei Vollbild).</summary>
        void StartScan()
        {
            if (_scanning) return;
            _scanning = true;
            _games.SetMessage("Suche Spiele ...");
            Invalidate();
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    GameScan.Run(true, () => IsDisposed, Desk.FullscreenActive,
                        res => OnUi(() => { _scan = res; _games.SetRows(res.Rows); Invalidate(); }),
                        row => OnUi(() => { _games.RowChanged(); Invalidate(new Rectangle(0, CardsY, Width, BarH)); }));
                }
                catch (Exception ex)
                {
                    Log.Write("  Spiele-Scan fehlgeschlagen: " + ex.Message, LogKind.Error);
                    OnUi(() => _games.SetMessage("Fehler: " + ex.Message));
                }
                finally
                {
                    _scanning = false;
                    OnUi(() => Invalidate());
                }
            }) { IsBackground = true, Priority = System.Threading.ThreadPriority.Lowest, Name = "spiele-scan" };
            t.SetApartmentState(System.Threading.ApartmentState.STA);   // Verknüpfungen lesen geht über COM
            t.Start();
        }

        void OnUi(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch { }
        }

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
            Ops.SaveBackup.Finished -= OnBackupFinished;
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
            // Fensterknöpfe mittig zum Kopfblock (Symbol 36 px ab y = Pad)
            int btnY = Pad + 18 - 12;
            _close.SetBounds(w - Pad - Btn, btnY, Btn, 24);
            _min.SetBounds(w - Pad - 2 * Btn - 4, btnY, Btn, 24);
            int tx = Pad + 46 + Math.Max(TextRenderer.MeasureText("xpserver", Theme.Title).Width, TextRenderer.MeasureText(SubTitle(), Theme.Ui).Width) + 24;
            _tabMain.SetBounds(tx, btnY, TabW, 24);
            _tabGames.SetBounds(tx + TabW + 6, btnY, TabW, 24);
            _tabSave.SetBounds(tx + 2 * (TabW + 6), btnY, TabW, 24);
            if (_tab == 2)
            {
                _dest.SetBounds(Pad, CardsY, w - 2 * Pad - 110 - 8, 46);
                _pick.SetBounds(w - Pad - 110, CardsY + 16, 110, 29);
                _go.SetBounds(Pad, SaveGoY, 240, 44);
                _back.SetBounds(w - Pad - BackW, SaveGoY + 7, BackW, 30);
                Invalidate();
                return;
            }
            if (_tab == 1)
            {
                int gy = CardsY + (BarH - 16) / 2 + 1;
                _filter.SetBounds(Pad + 30, gy, FilterW - 40, 16);
                _hint.SetBounds(Pad + 30, gy, FilterW - 40, 16);
                _rescan.SetBounds(w - Pad - ClearW, CardsY, ClearW, BarH);
                int top = CardsY + BarH + Gap;
                _games.SetBounds(Pad + LogInset, top + LogInset, w - 2 * Pad - 2 * LogInset, h - Foot - top - 2 * LogInset);
                Invalidate();
                return;
            }
            int ty = BarY + (BarH - 16) / 2 + 1;
            _filter.SetBounds(Pad + 30, ty, FilterW - 40, 16);
            _hint.SetBounds(Pad + 30, ty, FilterW - 40, 16);
            _errors.SetBounds(Pad + FilterW + 8, BarY, ErrW, BarH);
            _clear.SetBounds(w - Pad - ClearW, BarY, ClearW, BarH);
            _lock.SetBounds(w - Pad - ClearW - 8 - LockW, BarY, LockW, BarH);
            // Log sitzt mit kleinem Abstand in einer abgerundeten Fläche (gezeichnet in OnPaint)
            _log.SetBounds(Pad + LogInset, LogY + LogInset, w - 2 * Pad - 2 * LogInset, h - Foot - LogY - 2 * LogInset);
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
            TextRenderer.DrawText(g, SubTitle(), Theme.Ui, new Point(Pad + 47, 36), Theme.Dim, TextFormatFlags.NoPadding);
            string status = Server.Locked ? "GESPERRT" : busy ? "ARBEITET" : "LAUSCHT";
            Color sc = Server.Locked ? Theme.Red : busy ? Theme.Amber : Theme.Accent;
            string pillText = status + "  :" + Program.ShownPort;
            var psz = TextRenderer.MeasureText(pillText, Theme.UiBold);
            var pill = new RectangleF(w - Pad - 2 * Btn - 4 - Gap - (psz.Width + 26), Pad + 18 - 11, psz.Width + 26, 22);
            using (var p = Theme.Round(pill, 11))
            using (var b = new SolidBrush(Theme.Alpha(sc, 36))) g.FillPath(b, p);
            using (var b = new SolidBrush(sc)) g.FillEllipse(b, pill.X + 9, pill.Y + 8, 6, 6);
            TextRenderer.DrawText(g, pillText, Theme.UiBold, new Point((int)pill.X + 19, (int)pill.Y + 4), sc, TextFormatFlags.NoPadding);

            if (_tab == 1) { PaintGames(g, w, h); Grip(g, w, h); return; }
            if (_tab == 2) { PaintSave(g, w, h); Grip(g, w, h); return; }

            // Karten
            var up = DateTime.Now - Server.Started;
            string upText = up.TotalDays >= 1 ? string.Format("{0}d {1}h", (int)up.TotalDays, up.Hours) :
                            up.TotalHours >= 1 ? string.Format("{0}h {1:00}m", (int)up.TotalHours, up.Minutes) :
                            string.Format("{0}m {1:00}s", up.Minutes, up.Seconds);
            var nets = new List<string>();
            foreach (var n in Program.Allow) nets.Add(Program.Show(n.ToString()));
            var cards = new[] {
                new[] { "BEFEHLE", Server.OpsTotal.ToString(), "seit Start" },
                new[] { "FEHLER", Server.ErrorsTotal.ToString(), Server.ErrorsTotal == 0 ? "alles sauber" : "siehe Log" },
                new[] { "VERBINDUNGEN", Server.ClientsNow.ToString(), Server.ClientsNow == 1 ? "Client aktiv" : "Clients aktiv" },
                new[] { "LAUFZEIT", upText, Server.Started.ToString("dd.MM. HH:mm") },
                new[] { "ERLAUBT", nets.Count > 0 ? nets[0] : "-", nets.Count > 1 ? "+" + (nets.Count - 1) + " weitere" : "Netz" },
            };
            int inner = w - 2 * Pad;
            for (int i = 0; i < cards.Length; i++)
            {
                // Rest der Division verteilen, damit die letzte Karte bündig rechts endet
                int x0 = Pad + (int)Math.Round(i * (inner + Gap) / 5.0);
                int x1 = Pad + (int)Math.Round((i + 1) * (inner + Gap) / 5.0) - Gap;
                var r = new RectangleF(x0, CardsY, x1 - x0, CardH);
                Surface(g, r);
                Color vc = i == 1 && Server.ErrorsTotal > 0 ? Theme.Red : i == 2 && Server.ClientsNow > 0 ? Theme.Amber : Theme.Text;
                int cx = (int)r.X + Inner, cy = (int)r.Y;
                TextRenderer.DrawText(g, cards[i][0], Theme.Small, new Point(cx, cy + 10), Theme.Dim, TextFormatFlags.NoPadding);
                var big = cards[i][1].Length > 10 ? Theme.UiBold : Theme.Big;
                TextRenderer.DrawText(g, cards[i][1], big, new Rectangle(cx - 1, cy + 22, (int)r.Width - 2 * Inner + 2, 24), vc,
                    TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, cards[i][2], Theme.Small, new Point(cx, cy + CardH - 10 - 11), Theme.Faint, TextFormatFlags.NoPadding);
            }

            // Aktivität
            var sr = new RectangleF(Pad, SparkY, inner, SparkH);
            Surface(g, sr);
            TextRenderer.DrawText(g, "AKTIVITÄT  ·  letzte 30 Minuten", Theme.Small, new Point(Pad + Inner, (int)sr.Y + 10), Theme.Dim, TextFormatFlags.NoPadding);
            var buckets = _act.Minutes(30);
            int max = 1;
            foreach (var x in buckets) max = Math.Max(max, x);
            TextRenderer.DrawText(g, "max " + max + "/min", Theme.Small, new Rectangle(0, (int)sr.Y + 10, (int)sr.Right - Inner, 12), Theme.Faint,
                TextFormatFlags.Right | TextFormatFlags.NoPadding);
            float bw = (sr.Width - 2 * Inner + 3) / buckets.Length;
            float baseY = sr.Bottom - 10, hMax = sr.Height - 36;
            for (int i = 0; i < buckets.Length; i++)
            {
                float bh = buckets[i] == 0 ? 2 : Math.Max(4, hMax * buckets[i] / max);
                var br = new RectangleF(sr.X + Inner + i * bw, baseY - bh, bw - 3, bh);
                Color c = buckets[i] == 0 ? Theme.Panel2 : Theme.Mix(Theme.Accent, Theme.Amber, (float)buckets[i] / max);
                using (var p = Theme.Round(br, Math.Min(2.5f, br.Width / 2)))
                using (var b = new SolidBrush(c)) g.FillPath(b, p);
            }

            // Leiste über dem Log
            FilterBox(g, BarY);

            // Fläche ums Log, gleiche Form wie Karten und Aktivität
            Surface(g, new RectangleF(Pad, LogY, inner, h - Foot - LogY));

            // Fuß: nur der Log-Pfad, bündig mit den Flächen
            TextRenderer.DrawText(g, "Log: " + Program.Show(Log.FilePath), Theme.Small, new Rectangle(Pad, h - Foot, inner - 20, Foot), Theme.Faint,
                TextFormatFlags.NoPadding | TextFormatFlags.PathEllipsis | TextFormatFlags.VerticalCenter);
            Grip(g, w, h);
        }

        static void Grip(Graphics g, int w, int h)
        {
            using (var pen = new Pen(Theme.Faint))
                for (int i = 0; i < 3; i++) g.DrawLine(pen, w - 4 - i * 4, h - 3, w - 3, h - 4 - i * 4);
        }

        void FilterBox(Graphics g, int by)
        {
            var fr = new RectangleF(Pad + 0.5f, by + 0.5f, FilterW - 1, BarH - 1);
            using (var p = Theme.Round(fr, 6))
            {
                using (var b = new SolidBrush(Theme.Panel2)) g.FillPath(b, p);
                using (var pen = new Pen(_filter.Focused ? Theme.Accent : Theme.Border)) g.DrawPath(pen, p);
            }
            using (var pen = new Pen(Theme.Dim, 1.6f))
            {
                int my = by + BarH / 2;
                g.DrawEllipse(pen, Pad + 10, my - 6, 9, 9);
                g.DrawLine(pen, Pad + 18, my + 2, Pad + 22, my + 6);
            }
        }

        /// <summary>Spiele-Reiter: Filter, Zähler je Status, Liste, Datenbank-Stand im Fuß.</summary>
        void PaintGames(Graphics g, int w, int h)
        {
            int inner = w - 2 * Pad;
            FilterBox(g, CardsY);
            var res = _scan;
            int x = Pad + FilterW + 14;
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;
            if (res != null)
            {
                var n = new Dictionary<string, int>();
                foreach (var r in res.Rows) { int c; n.TryGetValue(r.Status, out c); n[r.Status] = c + 1; }
                foreach (var st in new[] { "patch", "unklar", "aktuell", "fehlt", "unbekannt" })
                {
                    int c;
                    if (!n.TryGetValue(st, out c)) continue;
                    string t = c + " " + GamesView.Label(st);
                    var sz = TextRenderer.MeasureText(g, t, Theme.UiBold, Size.Empty, TextFormatFlags.NoPadding);
                    Color tone = GamesView.Tone(st);
                    using (var b = new SolidBrush(tone)) g.FillEllipse(b, x, CardsY + BarH / 2 - 3, 6, 6);
                    TextRenderer.DrawText(g, t, Theme.UiBold, new Rectangle(x + 10, CardsY, sz.Width + 2, BarH), tone, flags);
                    x += sz.Width + 24;
                }
            }
            string state = _scanning ? (res == null ? "suche ..." : res.Pending > 0 ? (Desk.FullscreenActive() ? "pausiert (Vollbild)" : "prüfe md5, " + res.Pending + " offen") : "") :
                           res != null ? "geprüft " + res.When.ToString("HH:mm") : "";
            if (state != "")
                TextRenderer.DrawText(g, state, Theme.Small, new Rectangle(x, CardsY, Math.Max(10, w - Pad - ClearW - 12 - x), BarH), Theme.Faint,
                    flags | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
            int top = CardsY + BarH + Gap;
            Surface(g, new RectangleF(Pad, top, inner, h - Foot - top));
            string foot = res == null ? "Spiele-Datenbank wird beim ersten Prüfen geladen." :
                string.Format("Datenbank: Stand {0} ({1}, {2} Exes). Gesucht über GOG-Registry, Registry-Hinweise und Desktop-Verknüpfungen; unbekannte Exes werden nicht geprüft.",
                    res.Db.Stand, res.Db.Source, res.Db.All.Count);
            TextRenderer.DrawText(g, foot, Theme.Small, new Rectangle(Pad, h - Foot, inner - 20, Foot), Theme.Faint,
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        }

        /// <summary>Sicherungs-Reiter: Zustand des Zielordners, Knopf, Liste der Spiele mit Ordner und letztem Zip.</summary>
        void PaintSave(Graphics g, int w, int h)
        {
            int inner = w - 2 * Pad;
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, _saveState, Theme.Ui, new Rectangle(Pad + 1, CardsY + 52, inner, 16), _saveTone, flags);
            var run = Ops.SaveBackup.Running;
            string side = run != null ? "Sicherung läuft (seit " + run.When.ToString("HH:mm:ss") + ") ..." :
                "Ein Zip pro Spiel: <spiel>\\<spiel>_JJJJ-MM-TT_hh-mm-ss.zip, nur wenn sich etwas geändert hat.";
            TextRenderer.DrawText(g, side, Theme.Ui, new Rectangle(Pad + 240 + 16, SaveGoY, inner - 256 - BackW - 16, 44), run != null ? Theme.Amber : Theme.Dim,
                flags | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            int top = SaveListY;
            Surface(g, new RectangleF(Pad, top, inner, h - Foot - top));
            int y = top + Inner;
            TextRenderer.DrawText(g, "SPIELE MIT BEKANNTEN SPEICHERORTEN", Theme.Small, new Point(Pad + Inner, y), Theme.Dim, TextFormatFlags.NoPadding);
            y += 20;
            if (_saveRows == null)
                TextRenderer.DrawText(g, "lese ...", Theme.Ui, new Point(Pad + Inner, y), Theme.Faint, TextFormatFlags.NoPadding);
            else
                foreach (var r in _saveRows)
                {
                    if (y + 34 > h - Foot) break;
                    bool inst = r.Status == "installiert";
                    Color c = inst ? Theme.Accent : r.Status == "fehler" ? Theme.Red : Theme.Faint;
                    using (var b = new SolidBrush(c)) g.FillEllipse(b, Pad + Inner, y + 4, 7, 7);
                    TextRenderer.DrawText(g, r.Title, Theme.UiBold, new Rectangle(Pad + Inner + 16, y, 260, 16), Theme.Text, flags);
                    string last = r.Zip != null ? "zuletzt " + System.IO.Path.GetFileName(r.Zip) : inst ? "noch nicht gesichert" : r.Status;
                    TextRenderer.DrawText(g, last, Theme.Ui, new Rectangle(w - Pad - Inner - 300, y, 300, 16), r.Zip != null ? Theme.Dim : Theme.Faint,
                        flags | TextFormatFlags.Right);
                    string sub = inst ? Program.Show(r.SaveDir) + "  ·  " + r.Files + " Dateien" + (r.Version != null ? "  ·  " + r.Version : "") :
                                 r.Error ?? "";
                    TextRenderer.DrawText(g, sub, Theme.Small, new Rectangle(Pad + Inner + 16, y + 17, inner - 2 * Inner - 16, 13), Theme.Faint,
                        flags | TextFormatFlags.PathEllipsis);
                    y += 36;
                }
            TextRenderer.DrawText(g, "Auch per Befehl: xpctl.py saves backup | restore  ·  Speicherorte aus save-locations.json", Theme.Small,
                new Rectangle(Pad, h - Foot, inner - 20, Foot), Theme.Faint, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        }

        /// <summary>Einheitliche Fläche: Panel-Farbe, 1 px Rand, Radius 8, auf halbe Pixel gesetzt.</summary>
        static void Surface(Graphics g, RectangleF r)
        {
            using (var p = Theme.Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), 8))
            {
                using (var b = new SolidBrush(Theme.Panel)) g.FillPath(b, p);
                using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, p);
            }
        }

        protected override void OnActivated(EventArgs e) { base.OnActivated(e); Invalidate(); }
    }

    /// <summary>Flacher Knopf mit optionalem Ein/Aus-Zustand.</summary>
    public class Chip : Control
    {
        public bool Toggle, Ghost;
        public Font Face;                // eigene Schrift (z. B. großer Knopf), sonst Standard
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
            TextRenderer.DrawText(g, t, Face ?? (Ghost ? Theme.Title : Theme.Ui), new Rectangle(0, Ghost ? -1 : 0, Width, Height), _on ? acc : Ghost && _hover ? Theme.Text : Theme.Mix(Theme.Text, Theme.Dim, Ghost ? 0.5f : 0f),
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
