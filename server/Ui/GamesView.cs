using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace XpServer.Ui
{
    /// <summary>Spieleliste: Status, Spiel, installierte und neueste Version, Quelle. Selbst gezeichnet wie das Log.</summary>
    public class GamesView : Control
    {
        List<GameRow> _all = new List<GameRow>();
        List<GameRow> _view = new List<GameRow>();
        string _filter = "", _message = "Noch nicht geprüft.";
        int _top, _hover = -1, _dragOff;
        bool _drag;
        readonly ToolTip _tip = new ToolTip { InitialDelay = 400, ReshowDelay = 100, AutoPopDelay = 20000 };
        const int RowH = 20, HeadH = 22, BarW = 8, PadX = 8, TagW = 76, SrcW = 70, CheckW = 52;

        public GamesView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Panel;
        }

        public void SetRows(List<GameRow> rows) { _all = rows ?? new List<GameRow>(); Rebuild(); }
        public void SetMessage(string m) { _message = m; Invalidate(); }
        public void SetFilter(string f) { _filter = (f ?? "").Trim(); Rebuild(); }
        public void RowChanged() { Invalidate(); }

        void Rebuild()
        {
            _view = _filter.Length == 0 ? new List<GameRow>(_all) : _all.FindAll(r =>
                r.Name.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                Label(r.Status).IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                Path.GetFileName(r.Exe).IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0);
            _top = Math.Min(_top, MaxTop);
            Invalidate();
        }

        public static string Label(string status)
        {
            switch (status)
            {
                case "aktuell": return "aktuell";
                case "patch": return "Patch da";
                case "unklar": return "unklar";
                case "fehlt": return "Exe fehlt";
                default: return "unbekannt";
            }
        }

        public static Color Tone(string status)
        {
            switch (status)
            {
                case "aktuell": return Theme.Accent;
                case "patch": return Theme.Amber;
                case "unklar": return Theme.Blue;
                case "fehlt": return Theme.Red;
                default: return Theme.Dim;
            }
        }

        int Rows { get { return Math.Max(1, (Height - HeadH) / RowH); } }
        int MaxTop { get { return Math.Max(0, _view.Count - Rows); } }
        void ScrollTo(int t) { _top = Math.Max(0, Math.Min(MaxTop, t)); Invalidate(); }

        protected override void OnMouseWheel(MouseEventArgs e) { ScrollTo(_top - Math.Sign(e.Delta) * 3); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.PageUp) ScrollTo(_top - Rows);
            else if (e.KeyCode == Keys.PageDown) ScrollTo(_top + Rows);
            else if (e.KeyCode == Keys.Home) ScrollTo(0);
            else if (e.KeyCode == Keys.End) ScrollTo(MaxTop);
            else if (e.KeyCode == Keys.Up) ScrollTo(_top - 1);
            else if (e.KeyCode == Keys.Down) ScrollTo(_top + 1);
        }

        protected override bool IsInputKey(Keys k)
        {
            return k == Keys.Up || k == Keys.Down || k == Keys.PageUp || k == Keys.PageDown || base.IsInputKey(k);
        }

        Rectangle Thumb()
        {
            int track = Height - HeadH - 8;
            if (_view.Count <= Rows) return Rectangle.Empty;
            int h = Math.Max(24, track * Rows / _view.Count);
            int y = HeadH + 4 + (int)((long)(track - h) * _top / Math.Max(1, MaxTop));
            return new Rectangle(Width - BarW - 2, y, BarW - 2, h);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            var t = Thumb();
            if (t.IsEmpty || e.X < Width - BarW - 4) return;
            if (t.Contains(e.Location)) { _drag = true; _dragOff = e.Y - t.Y; }
            else ScrollTo(_top + (e.Y < t.Y ? -Rows : Rows));
        }

        protected override void OnMouseUp(MouseEventArgs e) { _drag = false; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_drag)
            {
                var t = Thumb();
                int track = Height - HeadH - 8 - t.Height;
                if (track > 0) ScrollTo((int)((long)(e.Y - _dragOff - HeadH - 4) * MaxTop / track));
                return;
            }
            int i = e.Y < HeadH ? -1 : _top + (e.Y - HeadH) / RowH;
            if (i >= _view.Count) i = -1;
            if (i == _hover) return;
            _hover = i;
            Invalidate();
            _tip.SetToolTip(this, i < 0 ? "" : Detail(_view[i]));
        }

        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); }

        static string Detail(GameRow r)
        {
            var h = r.Hit;
            var lines = new List<string> { r.Name, "Exe: " + Program.Show(r.Exe) + (r.Size >= 0 ? "  (" + r.Size.ToString("N0") + " Byte)" : "") };
            if (h != null)
            {
                lines.Add("Installiert: " + h.Version + (h.Confidence != null ? "  [Sicherheit " + h.Confidence + "]" : ""));
                lines.Add("Neuester: " + h.Latest);
            }
            if (r.GogVer != null) lines.Add("GOG-Registry: " + r.GogVer);
            lines.Add("md5: " + (r.Md5 ?? (h == null ? "nicht geprüft (unbekannt)" : "wird geprüft ...")));
            if (r.Problem != null) lines.Add("Hinweis: " + r.Problem);
            return string.Join("\n", lines.ToArray());
        }

        static string Src(string s) { return s == "gog" ? "GOG" : s == "reg" ? "Registry" : "Desktop"; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Panel);
            Theme.Smooth(g);
            int right = Width - BarW - 8;
            int flex = Math.Max(60, right - PadX - TagW - 10 - SrcW - CheckW);
            int nameW = flex * 36 / 100, verW = flex * 30 / 100, latW = flex - nameW - verW;
            int xName = PadX + TagW + 10, xVer = xName + nameW, xLat = xVer + verW, xSrc = xLat + latW, xChk = xSrc + SrcW;
            var hf = TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, "STATUS", Theme.Small, new Rectangle(PadX, 0, TagW, HeadH), Theme.Dim, hf);
            TextRenderer.DrawText(g, "SPIEL", Theme.Small, new Rectangle(xName, 0, nameW, HeadH), Theme.Dim, hf);
            TextRenderer.DrawText(g, "INSTALLIERT", Theme.Small, new Rectangle(xVer, 0, verW, HeadH), Theme.Dim, hf);
            TextRenderer.DrawText(g, "NEUESTER STAND", Theme.Small, new Rectangle(xLat, 0, latW, HeadH), Theme.Dim, hf);
            TextRenderer.DrawText(g, "GEFUNDEN", Theme.Small, new Rectangle(xSrc, 0, SrcW, HeadH), Theme.Dim, hf);
            TextRenderer.DrawText(g, "MD5", Theme.Small, new Rectangle(xChk, 0, CheckW, HeadH), Theme.Dim, hf);
            using (var pen = new Pen(Theme.Border)) g.DrawLine(pen, PadX, HeadH - 1, right, HeadH - 1);

            if (_view.Count == 0)
            {
                TextRenderer.DrawText(g, _all.Count > 0 ? "Keine passenden Spiele." : _message, Theme.Ui,
                    new Rectangle(0, HeadH, Width, Height - HeadH), Theme.Faint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            int y = HeadH;
            for (int i = _top; i < _view.Count && y < Height; i++, y += RowH)
            {
                var r = _view[i];
                var h = r.Hit;
                if (i == _hover || i % 2 == 1)
                    using (var p = Theme.Round(new RectangleF(0, y, right + 2, RowH), 4))
                    using (var b = new SolidBrush(i == _hover ? Theme.Panel2 : Color.FromArgb(29, 32, 40))) g.FillPath(b, p);
                Color tone = Tone(r.Status);
                var pill = new RectangleF(PadX, y + 3, TagW, RowH - 6);
                using (var p = Theme.Round(pill, 6))
                using (var b = new SolidBrush(Theme.Alpha(tone, 38))) g.FillPath(b, p);
                TextRenderer.DrawText(g, Label(r.Status), Theme.UiBold, Rectangle.Round(pill), tone,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                bool unknown = h == null;
                TextRenderer.DrawText(g, r.Name, Theme.Ui, new Rectangle(xName, y, nameW - 8, RowH), unknown ? Theme.Dim : Theme.Text, hf);
                string ver = h != null ? h.Version : r.Problem ?? Path.GetFileName(r.Exe);
                if (h != null && h.Confidence == "niedrig") ver = "? " + ver;
                TextRenderer.DrawText(g, ver, Theme.Ui, new Rectangle(xVer, y, verW - 8, RowH), unknown ? Theme.Faint : Theme.Text, hf);
                string lat = h != null ? h.Latest : "";
                TextRenderer.DrawText(g, lat, Theme.Ui, new Rectangle(xLat, y, latW - 8, RowH),
                    r.Status == "patch" ? Theme.Mix(Theme.Amber, Theme.Text, 0.4f) : Theme.Dim, hf);
                TextRenderer.DrawText(g, Src(r.Source), Theme.Small, new Rectangle(xSrc, y, SrcW, RowH), Theme.Faint, hf);
                string chk = unknown ? "" : r.Problem != null ? "Fehler" : r.Confirmed ? "geprüft" : r.Md5 == null ? "..." : "anders";
                TextRenderer.DrawText(g, chk, Theme.Small, new Rectangle(xChk, y, CheckW, RowH),
                    r.Confirmed ? Theme.Accent : r.Problem != null ? Theme.Red : Theme.Faint, hf);
            }
            var th = Thumb();
            if (!th.IsEmpty)
            {
                using (var p = Theme.Round(new RectangleF(th.X, HeadH + 4, th.Width, Height - HeadH - 8), 3))
                using (var b = new SolidBrush(Theme.Panel2)) g.FillPath(b, p);
                using (var p = Theme.Round(th, 3))
                using (var b = new SolidBrush(_drag ? Theme.Dim : Theme.Faint)) g.FillPath(b, p);
            }
        }
    }
}
