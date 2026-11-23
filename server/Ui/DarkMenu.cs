using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XpServer.Ui
{
    /// <summary>Dunkles Kontextmenü mit abgerundeter Hervorhebung.</summary>
    public class DarkMenu : ToolStripProfessionalRenderer
    {
        public DarkMenu() : base(new Colors()) { RoundedEdges = false; }

        class Colors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.Panel; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Panel; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Panel; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Panel; } }
            public override Color MenuBorder { get { return Theme.Border; } }
            public override Color MenuItemBorder { get { return Theme.Panel2; } }
            public override Color MenuItemSelected { get { return Theme.Panel2; } }
            public override Color SeparatorDark { get { return Theme.Border; } }
            public override Color SeparatorLight { get { return Theme.Panel; } }
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics;
            Theme.Smooth(g);
            var r = new RectangleF(3, 1, e.Item.Width - 6, e.Item.Height - 2);
            using (var p = Theme.Round(r, 4))
            using (var b = new SolidBrush(Theme.Panel2)) g.FillPath(b, p);
            using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 4, 5, 2, e.Item.Height - 10);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if ((e.Item.Tag as string) == "head")
            {
                // Der Basis-Renderer färbt deaktivierte Einträge grau, die Kopfzeile selbst zeichnen
                TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, Theme.Accent, e.TextFormat);
                return;
            }
            e.TextColor = !e.Item.Enabled ? Theme.Dim : Theme.Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            Theme.Smooth(g);
            var r = e.ImageRectangle;
            r.Inflate(-1, -1);
            using (var p = Theme.Round(r, 3))
            using (var b = new SolidBrush(Theme.Alpha(Theme.Accent, 60))) g.FillPath(b, p);
            using (var pen = new Pen(Theme.Accent, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLines(pen, new[] { new PointF(r.X + r.Width * 0.25f, r.Y + r.Height * 0.52f),
                    new PointF(r.X + r.Width * 0.43f, r.Y + r.Height * 0.70f), new PointF(r.X + r.Width * 0.76f, r.Y + r.Height * 0.32f) });
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Dim;
            base.OnRenderArrow(e);
        }
    }
}
