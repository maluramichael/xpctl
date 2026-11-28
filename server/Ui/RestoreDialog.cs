using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace XpServer.Ui
{
    /// <summary>Reiter Sicherung, Knopf "Zurückholen ...": Spiel und Stand wählen, Probelauf zeigt, was sich
    /// ändert, nach Bestätigung holt <see cref="Ops.SaveBackup.RunRestore"/> den Stand zurück. Gelesen und
    /// geschrieben wird nur im Hintergrund (Netzlaufwerke können hängen).</summary>
    public class RestoreDialog : DarkDialog
    {
        readonly ComboBox _game = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
        readonly ListBox _list = new ListBox { BorderStyle = BorderStyle.None, IntegralHeight = false };
        readonly Label _state;
        readonly Chip _go;
        readonly List<Ops.SaveBackup.Loc> _games;
        List<Ops.SaveBackup.Stand> _stands = new List<Ops.SaveBackup.Stand>();
        bool _busy;

        public RestoreDialog() : base("Spielstand zurückholen", "aus den Zips im Zielordner", 560, 430)
        {
            _games = Ops.SaveBackup.Db();
            foreach (var g in _games) _game.Items.Add(g.Title + "  (" + g.Id + ")");
            foreach (Control c in new Control[] { _game, _list }) { c.BackColor = Theme.Panel2; c.ForeColor = Theme.Text; c.Font = Theme.Ui; }
            _game.SetBounds(20, Top0, ClientSize.Width - 40, 22);
            _list.SetBounds(20, Top0 + 32, ClientSize.Width - 40, 190);
            Controls.Add(_game);
            Controls.Add(_list);
            AddText("Vorher wird der jetzige Stand gesichert. Danach liegen im Spielstand-Ordner genau die Dateien des Zips: " +
                    "geänderte werden ersetzt, später dazugekommene gelöscht. Das Spiel muss beendet sein.", Top0 + 230, 32, Theme.Dim);
            _state = AddText("", Top0 + 268, 34, Theme.Faint);
            _go = AddButton("Zurückholen ...", 18, true, DialogResult.None);
            _go.Click += (s, e) => Start();
            AddButton("Schließen", 160, false, DialogResult.Cancel);
            _game.SelectedIndexChanged += (s, e) => ReadStands(null);
            _list.DoubleClick += (s, e) => Start();
            if (_games.Count > 0) _game.SelectedIndex = 0;
        }

        Ops.SaveBackup.Loc Game { get { return _game.SelectedIndex < 0 ? null : _games[_game.SelectedIndex]; } }

        void OnUi(Action a) { if (IsDisposed || !IsHandleCreated) return; try { BeginInvoke(a); } catch { } }

        void Bg(Action a) { new System.Threading.Thread(() => a()) { IsBackground = true, Name = "zurückholen" }.Start(); }

        static string N(int n, string one) { return n + " " + (n == 1 ? one : one + "en"); }

        void Say(string text, System.Drawing.Color c) { _state.Text = text; _state.ForeColor = c; }

        /// <summary>Sicherungen des gewählten Spiels lesen; done: Meldung danach statt der Anzahl.</summary>
        void ReadStands(string done)
        {
            var g = Game;
            if (g == null) return;
            _list.Items.Clear();
            Say("lese Sicherungen ...", Theme.Faint);
            Bg(() =>
            {
                List<Ops.SaveBackup.Stand> l = null; string err = null;
                try { l = Ops.SaveBackup.Stands(g.Id, null); }
                catch (Exception e) { err = e.Message; }
                OnUi(() =>
                {
                    if (Game != g) return;
                    _list.Items.Clear();
                    if (err != null) { Say(err, Theme.Red); return; }
                    _stands = l;
                    foreach (var s in l) _list.Items.Add(s.When + "     " + (s.Files < 0 ? "unlesbar" : s.Files + " Dateien") + "     " + s.Name);
                    if (l.Count > 0) _list.SelectedIndex = 0;
                    if (done != null) Say(done, Theme.Accent);
                    else Say(l.Count == 0 ? "Keine Sicherung von " + g.Title + " im Zielordner." : l.Count + (l.Count == 1 ? " Sicherung." : " Sicherungen, neueste oben."), Theme.Faint);
                });
            });
        }

        void Start()
        {
            var g = Game;
            int i = _list.SelectedIndex;
            if (_busy || g == null || i < 0 || i >= _stands.Count) return;
            var st = _stands[i];
            _busy = true;
            Say("Probelauf ...", Theme.Faint);
            Bg(() =>
            {
                Ops.SaveBackup.RestoreResult dry = null; string err = null;
                try { dry = Ops.SaveBackup.RunRestore(g.Id, st.Name, null, true); }
                catch (Exception e) { err = e.Message; }
                OnUi(() =>
                {
                    if (err != null) { _busy = false; Say(err, Theme.Red); return; }
                    int w = dry.Changes.Written.Count, rm = dry.Changes.Removed.Count;
                    if (w == 0 && rm == 0) { _busy = false; Say("Der Ordner entspricht schon diesem Stand, nichts zu tun.", Theme.Accent); return; }
                    string msg = g.Title + " auf " + st.When + " zurücksetzen? " + N(w, "Datei") + " ersetzen" + (rm > 0 ? ", " + N(rm, "Datei") + " löschen" : "") + ".";
                    if (!Dialogs.Confirm("Zurückholen", msg)) { _busy = false; Say("Abgebrochen, nichts geändert.", Theme.Faint); return; }
                    Say("hole zurück ...", Theme.Amber);
                    Bg(() =>
                    {
                        Ops.SaveBackup.RestoreResult res = null; string err2 = null;
                        try { res = Ops.SaveBackup.RunRestore(g.Id, st.Name, null, false); }
                        catch (Exception e) { err2 = e.Message; }
                        OnUi(() =>
                        {
                            _busy = false;
                            if (err2 != null) { Say(err2, Theme.Red); return; }
                            ReadStands("Zurückgeholt und Byte für Byte geprüft: " + N(res.Changes.Written.Count, "Datei") + " ersetzt, " + res.Changes.Removed.Count + " gelöscht." +
                                (res.BackupZip != null ? " Stand vorher: " + System.IO.Path.GetFileName(res.BackupZip) : ""));
                        });
                    });
                });
            });
        }
    }
}
