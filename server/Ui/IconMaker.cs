using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace XpServer.Ui
{
    public enum TrayState { Idle, Busy, Error, Locked }

    /// <summary>Zeichnet das Symbol: dunkle Kachel mit Prompt-Zeichen, Farbe und Ring je Zustand.</summary>
    public static class IconMaker
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        public static Bitmap Draw(int size, TrayState st, float phase)
        {
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                Theme.Smooth(g);
                g.Clear(Color.Transparent);
                float s = size;
                Color acc = st == TrayState.Busy ? Theme.Amber : st == TrayState.Error ? Theme.Red :
                            st == TrayState.Locked ? Theme.Dim : Theme.Accent;
                var tile = new RectangleF(0.5f, 0.5f, s - 1.5f, s - 1.5f);
                using (var path = Theme.Round(tile, s * 0.22f))
                {
                    using (var br = new LinearGradientBrush(tile, Color.FromArgb(44, 50, 62), Color.FromArgb(16, 18, 23), 90f))
                        g.FillPath(br, path);
                    using (var pen = new Pen(Theme.Alpha(acc, 210), Math.Max(1f, s / 16f)))
                        g.DrawPath(pen, path);
                }
                // Prompt ">"
                float w = Math.Max(1.6f, s / 8.5f);
                using (var pen = new Pen(acc, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    g.DrawLines(pen, new[] {
                        new PointF(s * 0.24f, s * 0.30f), new PointF(s * 0.47f, s * 0.50f), new PointF(s * 0.24f, s * 0.70f) });
                    // Cursor "_", blinkt bei Arbeit
                    if (st != TrayState.Busy || Math.Sin(phase * Math.PI * 2) > -0.2)
                        g.DrawLine(pen, s * 0.55f, s * 0.71f, s * 0.78f, s * 0.71f);
                }
                if (st == TrayState.Busy)
                {
                    // umlaufender Bogen
                    using (var pen = new Pen(Theme.Alpha(Theme.Amber, 230), Math.Max(1.2f, s / 12f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(pen, s * 0.12f, s * 0.12f, s * 0.76f, s * 0.76f, phase * 360f, 70f);
                }
                if (st == TrayState.Error || st == TrayState.Locked)
                {
                    float d = s * 0.36f;
                    var dot = new RectangleF(s - d - 0.5f, -0.0f, d, d);
                    using (var br = new SolidBrush(st == TrayState.Error ? Theme.Red : Theme.Dim)) g.FillEllipse(br, dot);
                    using (var pen = new Pen(Theme.Bg, Math.Max(1f, s / 16f))) g.DrawEllipse(pen, dot);
                }
            }
            return bmp;
        }

        /// <summary>Schreibt eine .ico-Datei (16, 24, 32, 48 px, 32 Bit mit Alpha) für das Exe-Symbol.
        /// Aufruf: xpserver.exe --write-icon DATEI (einmalig auf Windows, Ergebnis liegt als Ui/xpserver.ico im Repo).</summary>
        public static void WriteIco(string path)
        {
            int[] sizes = { 16, 24, 32, 48 };
            var images = new System.Collections.Generic.List<byte[]>();
            foreach (int sz in sizes)
            {
                using (var bmp = Draw(sz, TrayState.Idle, 0))
                using (var ms = new System.IO.MemoryStream())
                using (var w = new System.IO.BinaryWriter(ms))
                {
                    int maskRow = ((sz + 31) / 32) * 4;
                    w.Write(40); w.Write(sz); w.Write(sz * 2); w.Write((short)1); w.Write((short)32);
                    w.Write(0); w.Write(sz * sz * 4 + maskRow * sz); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                    for (int y = sz - 1; y >= 0; y--)
                        for (int x = 0; x < sz; x++)
                        {
                            var c = bmp.GetPixel(x, y);
                            w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                        }
                    for (int y = sz - 1; y >= 0; y--)
                    {
                        var row = new byte[maskRow];
                        for (int x = 0; x < sz; x++)
                            if (bmp.GetPixel(x, y).A < 128) row[x / 8] |= (byte)(0x80 >> (x % 8));
                        w.Write(row);
                    }
                    w.Flush();
                    images.Add(ms.ToArray());
                }
            }
            using (var f = new System.IO.BinaryWriter(System.IO.File.Create(path)))
            {
                f.Write((short)0); f.Write((short)1); f.Write((short)sizes.Length);
                int off = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    f.Write((byte)(sizes[i] % 256)); f.Write((byte)(sizes[i] % 256)); f.Write((byte)0); f.Write((byte)0);
                    f.Write((short)1); f.Write((short)32); f.Write(images[i].Length); f.Write(off);
                    off += images[i].Length;
                }
                foreach (var im in images) f.Write(im);
            }
        }

        /// <summary>Icon aus Bitmap; das alte Handle wird freigegeben.</summary>
        public static Icon ToIcon(Bitmap bmp, ref IntPtr lastHandle)
        {
            IntPtr h = bmp.GetHicon();
            var ico = Icon.FromHandle(h);
            if (lastHandle != IntPtr.Zero) DestroyIcon(lastHandle);
            lastHandle = h;
            return ico;
        }
    }
}
