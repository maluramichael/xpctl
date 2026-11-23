using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace XpServer.Ui
{
    /// <summary>Farben und Schriften. XP hat kein Segoe UI/Consolas: Tahoma und Lucida Console.</summary>
    public static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(17, 19, 24);
        public static readonly Color Panel = Color.FromArgb(26, 29, 36);
        public static readonly Color Panel2 = Color.FromArgb(33, 37, 46);
        public static readonly Color Border = Color.FromArgb(44, 49, 60);
        public static readonly Color Text = Color.FromArgb(222, 226, 233);
        public static readonly Color Dim = Color.FromArgb(128, 137, 153);
        public static readonly Color Faint = Color.FromArgb(78, 85, 99);
        public static readonly Color Accent = Color.FromArgb(61, 220, 151);    // grün-türkis
        public static readonly Color Amber = Color.FromArgb(245, 165, 36);
        public static readonly Color Red = Color.FromArgb(255, 93, 93);
        public static readonly Color Blue = Color.FromArgb(90, 169, 255);
        public static readonly Color Violet = Color.FromArgb(180, 140, 255);
        public static readonly Color Pink = Color.FromArgb(255, 121, 198);

        public static readonly Font Ui = new Font("Tahoma", 8.25f);
        public static readonly Font UiBold = new Font("Tahoma", 8.25f, FontStyle.Bold);
        public static readonly Font Title = new Font("Tahoma", 11f, FontStyle.Bold);
        public static readonly Font Big = new Font("Tahoma", 15f, FontStyle.Bold);
        public static readonly Font Small = new Font("Tahoma", 7f);
        public static readonly Font Mono = new Font("Lucida Console", 8.25f);

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static Color Alpha(Color c, int a) { return Color.FromArgb(a, c); }

        /// <summary>Farbe für eine Op im Log.</summary>
        public static Color OpColor(string op)
        {
            switch (op)
            {
                case "run": case "kill": case "install": case "uninstall": case "update": return Amber;
                case "put": case "get": case "copy": case "rm": case "ls": case "cat": case "hash": return Blue;
                case "ui": case "shot": case "click": case "press": case "settext": case "key":
                case "type": case "focus": case "close": return Violet;
                case "mount": case "eject": case "ripiso": case "ripcd": case "toc": case "vol": case "disc": return Pink;
                default: return Accent;
            }
        }
    }
}
