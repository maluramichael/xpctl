using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace XpServer.Ui
{
    /// <summary>Live-Log: selbst gezeichnete Liste mit Zeit, Client, farbigem Op-Etikett und schmaler Scrollleiste.</summary>
    public class LogView : Control
    {
        readonly List<LogLine> _all = new List<LogLine>();
        List<LogLine> _view = new List<LogLine>();
        int _top;               // erste sichtbare Zeile
        bool _follow = true;    // am Ende kleben
        bool _drag;
        int _dragOff;
        string _filter = "";
        bool _onlyErrors;
        const int RowH = 18;
        const int BarW = 8;

        public LogView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Panel;
            Cursor = Cursors.Default;
        }

        public void SetLines(IEnumerable<LogLine> lines)
        {
            _all.Clear();
            _all.AddRange(lines);
            Rebuild();
        }

        public void Add(LogLine l)
        {
            _all.Add(l);
            if (_all.Count > 3000) _all.RemoveRange(0, _all.Count - 3000);
            if (Match(l)) _view.Add(l);
            if (_view.Count > 3000) _view.RemoveRange(0, _view.Count - 3000);
            if (_follow) _top = MaxTop;
            Invalidate();
        }

        public void SetFilter(string text, bool onlyErrors)
        {
            _filter = (text ?? "").Trim();
            _onlyErrors = onlyErrors;
            Rebuild();
        }

        public void ClearView() { _all.Clear(); _view.Clear(); _top = 0; _follow = true; Invalidate(); }

        bool Match(LogLine l)
        {
            if (_onlyErrors && l.Kind != LogKind.Error && l.Kind != LogKind.Denied) return false;
            if (_filter.Length == 0) return true;
            return l.Text.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (l.Client ?? "").IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void Rebuild()
        {
            _view = _all.FindAll(Match);
            _top = MaxTop;
            _follow = true;
            Invalidate();
        }

        int Rows { get { return Math.Max(1, (Height - 8) / RowH); } }
        int MaxTop { get { return Math.Max(0, _view.Count - Rows); } }

        void ScrollTo(int top)
        {
            _top = Math.Max(0, Math.Min(MaxTop, top));
            _follow = _top >= MaxTop;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollTo(_top - Math.Sign(e.Delta) * 3);
        }

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
            int track = Height - 8;
            if (_view.Count <= Rows) return Rectangle.Empty;
            int h = Math.Max(24, track * Rows / _view.Count);
            int y = 4 + (int)((long)(track - h) * _top / Math.Max(1, MaxTop));
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

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_drag) return;
            var t = Thumb();
            int track = Height - 8 - t.Height;
            if (track <= 0) return;
            ScrollTo((int)((long)(e.Y - _dragOff - 4) * MaxTop / track));
        }

        protected override void OnMouseUp(MouseEventArgs e) { _drag = false; }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_follow) _top = MaxTop;
        }

        static string OpOf(LogLine l)
        {
            string t = l.Text;
            int i = 0;
            while (i < t.Length && t[i] != ' ' && t[i] != ':') i++;
            return t.Substring(0, i);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Panel);
            Theme.Smooth(g);
            if (_view.Count == 0)
            {
                TextRenderer.DrawText(g, _filter.Length > 0 || _onlyErrors ? "Keine passenden Einträge." : "Noch keine Befehle. Warte auf xpctl ...",
                    Theme.Ui, new Rectangle(0, 0, Width, Height), Theme.Faint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            int y = 4;
            int textRight = Width - BarW - 8;
            for (int i = _top; i < _view.Count && y < Height; i++, y += RowH)
            {
                var l = _view[i];
                bool err = l.Kind == LogKind.Error || l.Kind == LogKind.Denied;
                if (err)
                    using (var b = new SolidBrush(Theme.Alpha(Theme.Red, 26))) g.FillRectangle(b, 0, y, textRight + 4, RowH);
                else if (i % 2 == 1)
                    using (var b = new SolidBrush(Color.FromArgb(29, 32, 40))) g.FillRectangle(b, 0, y, textRight + 4, RowH);

                int x = 10;
                TextRenderer.DrawText(g, l.Time.ToString("HH:mm:ss"), Theme.Mono, new Point(x, y + 3), Theme.Faint, TextFormatFlags.NoPrefix);
                x += 70;

                string text = l.Text;
                Color tag = Theme.Dim;
                string tagText = null;
                switch (l.Kind)
                {
                    case LogKind.Op: tagText = OpOf(l); tag = Theme.OpColor(tagText); text = text.Substring(tagText.Length).TrimStart(':', ' '); break;
                    case LogKind.Error: tagText = "FEHLER"; tag = Theme.Red; text = text.Trim(); if (text.StartsWith("Fehler: ")) text = text.Substring(8); break;
                    case LogKind.Denied: tagText = "ABGELEHNT"; tag = Theme.Red; text = text.Replace("ABGELEHNT ", ""); break;
                    case LogKind.Ok: tagText = "ok"; tag = Theme.Accent; text = text.Trim(); break;
                    case LogKind.System: tagText = "server"; tag = Theme.Accent; break;
                }
                if (tagText != null)
                {
                    var sz = TextRenderer.MeasureText(g, tagText, Theme.UiBold, Size.Empty, TextFormatFlags.NoPadding);
                    var pill = new RectangleF(x, y + 2, Math.Max(sz.Width + 12, 68), RowH - 4);
                    using (var p = Theme.Round(pill, 6))
                    using (var b = new SolidBrush(Theme.Alpha(tag, 38))) g.FillPath(b, p);
                    TextRenderer.DrawText(g, tagText, Theme.UiBold, Rectangle.Round(pill), tag,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    x += (int)pill.Width + 8;
                }
                if (l.Client != null && l.Kind == LogKind.Op)
                {
                    TextRenderer.DrawText(g, l.Client, Theme.Small, new Point(x, y + 4), Theme.Dim, TextFormatFlags.NoPrefix);
                    x += 84;
                }
                TextRenderer.DrawText(g, text, Theme.Mono, new Rectangle(x, y + 3, Math.Max(10, textRight - x), RowH - 3),
                    err ? Theme.Mix(Theme.Red, Theme.Text, 0.35f) : Theme.Text,
                    TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
            var th = Thumb();
            if (!th.IsEmpty)
            {
                using (var p = Theme.Round(new RectangleF(th.X, 4, th.Width, Height - 8), 3))
                using (var b = new SolidBrush(Theme.Panel2)) g.FillPath(b, p);
                using (var p = Theme.Round(th, 3))
                using (var b = new SolidBrush(_drag ? Theme.Dim : Theme.Faint)) g.FillPath(b, p);
            }
        }
    }
}
