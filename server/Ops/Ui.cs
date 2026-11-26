using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace XpServer
{
    /// <summary>Fenster und Eingaben: ui, shot, click, press, settext, key, type, focus, close.</summary>
    public static class UiOps
    {
        delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter;
            public uint biClrUsed, biClrImportant;
        }

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
        [DllImport("user32.dll")] static extern int GetWindowLongW(IntPtr h, int idx);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessageTimeoutW(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out UIntPtr res);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] static extern uint MapVirtualKeyW(uint code, uint type);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern short VkKeyScanW(char ch);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("psapi.dll", CharSet = CharSet.Unicode)] static extern uint GetModuleBaseNameW(IntPtr p, IntPtr m, StringBuilder sb, uint n);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int sw, int sh, uint rop);
        [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bih, uint usage);

        static readonly Dictionary<string, int> VkNames = MakeVk();
        static readonly Dictionary<string, int> VkMods = new Dictionary<string, int> {
            { "CTRL", 0x11 }, { "CONTROL", 0x11 }, { "ALT", 0x12 }, { "SHIFT", 0x10 }, { "WIN", 0x5B } };

        static Dictionary<string, int> MakeVk()
        {
            var d = new Dictionary<string, int> {
                { "ENTER", 0x0D }, { "RETURN", 0x0D }, { "TAB", 0x09 }, { "ESC", 0x1B }, { "ESCAPE", 0x1B },
                { "SPACE", 0x20 }, { "BACKSPACE", 0x08 }, { "DELETE", 0x2E }, { "DEL", 0x2E }, { "INSERT", 0x2D },
                { "HOME", 0x24 }, { "END", 0x23 }, { "PGUP", 0x21 }, { "PGDN", 0x22 },
                { "LEFT", 0x25 }, { "UP", 0x26 }, { "RIGHT", 0x27 }, { "DOWN", 0x28 } };
            for (int i = 1; i <= 12; i++) d["F" + i] = 0x6F + i;
            return d;
        }

        static bool IsExt(int vk)
        {
            return (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E;
        }

        // ---------- Helfer ----------

        static long H(IntPtr h) { return (long)(uint)h.ToInt32(); }
        static IntPtr P(long v) { return new IntPtr(unchecked((int)(uint)v)); }
        static long NeedInt(Req r, string k) { r.Need(k); return r.Int(k); }

        static bool Send(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, uint timeout, out long res)
        {
            UIntPtr o;
            IntPtr ok = SendMessageTimeoutW(hwnd, msg, wp, lp, 0x0003, timeout, out o);
            res = (long)o.ToUInt32();
            return ok != IntPtr.Zero;
        }

        static string WText(IntPtr hwnd)
        {
            const int max = 400;
            IntPtr buf = Marshal.AllocHGlobal(max * 2);
            try
            {
                Marshal.WriteInt16(buf, 0);
                long res;
                if (!Send(hwnd, 0x000D, new IntPtr(max), buf, 300, out res)) return "";
                string s = Marshal.PtrToStringUni(buf, max);
                int z = s.IndexOf('\0');
                return z >= 0 ? s.Substring(0, z) : s;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static string WExe(uint pid)
        {
            IntPtr h = OpenProcess(0x0410, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(260);
                GetModuleBaseNameW(h, IntPtr.Zero, sb, 260);
                return sb.ToString();
            }
            finally { CloseHandle(h); }
        }

        static J WInfo(IntPtr hwnd, bool top)
        {
            var cls = new StringBuilder(256);
            GetClassNameW(hwnd, cls, 256);
            RECT r;
            GetWindowRect(hwnd, out r);
            var d = J.O("hwnd", H(hwnd), "class", cls.ToString(), "text", WText(hwnd),
                "rect", new List<object> { (long)r.left, (long)r.top, (long)r.right, (long)r.bottom },
                "visible", IsWindowVisible(hwnd), "enabled", IsWindowEnabled(hwnd));
            if (top)
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                d["pid"] = (long)pid;
                d["exe"] = WExe(pid);
                d["hung"] = IsHungAppWindow(hwnd);
                d["minimized"] = IsIconic(hwnd);
                d["owner"] = H(GetWindow(hwnd, 4));
            }
            else
            {
                d["id"] = (long)GetDlgCtrlID(hwnd);
                if (cls.ToString().ToLowerInvariant() == "button")
                {
                    int bstyle = GetWindowLongW(hwnd, -16) & 0xF;
                    if (bstyle == 2 || bstyle == 3 || bstyle == 4 || bstyle == 5 || bstyle == 6 || bstyle == 9)
                    {
                        long state;
                        if (Send(hwnd, 0x00F0, IntPtr.Zero, IntPtr.Zero, 300, out state)) d["checked"] = state;
                    }
                }
            }
            return d;
        }

        static List<IntPtr> WEnum(IntPtr parent, bool top)
        {
            var list = new List<IntPtr>();
            EnumProc cb = delegate (IntPtr h, IntPtr l) { if (h != IntPtr.Zero) list.Add(h); return true; };
            if (top) EnumWindows(cb, IntPtr.Zero); else EnumChildWindows(parent, cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return list;
        }

        // ---------- Ops ----------

        [Op("ui")]
        static J Ui(Conn c, Req r)
        {
            long fg = H(GetForegroundWindow());
            long target = Py.Truthy(r["hwnd"]) ? r.Int("hwnd") : fg;
            bool all = r.Bool("all");
            var wins = new List<object>();
            foreach (IntPtr h in WEnum(IntPtr.Zero, true))
            {
                if (!IsWindowVisible(h)) continue;
                J info = WInfo(h, true);
                if ((string)info["text"] != "" || all) wins.Add(info);
            }
            var controls = new List<object>();
            if (target != 0 && IsWindow(P(target)))
            {
                var kids = WEnum(P(target), false);
                for (int i = 0; i < kids.Count && i < 600; i++)
                {
                    IntPtr h = kids[i];
                    J ci = WInfo(h, false);
                    ci["parent"] = H(GetParent(h));
                    if ((bool)ci["visible"] || all) controls.Add(ci);
                }
            }
            return J.Ok("screen", new List<object> { (long)GetSystemMetrics(0), (long)GetSystemMetrics(1) },
                "foreground", fg, "target", target, "windows", wins, "controls", controls);
        }

        static void PutBE(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
        }

        static byte[] ZlibCompress(byte[] data)
        {
            var ms = new MemoryStream();
            ms.WriteByte(0x78); ms.WriteByte(0x9C);
            using (var ds = new DeflateStream(ms, CompressionMode.Compress, true))
                ds.Write(data, 0, data.Length);
            uint a = 1, b = 0;
            int i = 0;
            while (i < data.Length)
            {
                int n = Math.Min(5552, data.Length - i);
                for (int j = 0; j < n; j++) { a += data[i + j]; b += a; }
                a %= 65521; b %= 65521;
                i += n;
            }
            PutBE(ms, (b << 16) | a);
            return ms.ToArray();
        }

        static long ToInt(object o)
        {
            if (o is long) return (long)o;
            if (o is double) return (long)(double)o;
            if (o is bool) return (bool)o ? 1 : 0;
            return long.Parse(Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture).Trim());
        }

        [Op("shot")]
        static J Shot(Conn c, Req r)
        {
            int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
            int l, t, rr, b;
            if (Py.Truthy(r["hwnd"]))
            {
                RECT rc;
                GetWindowRect(P(r.Int("hwnd")), out rc);
                l = rc.left; t = rc.top; rr = rc.right; b = rc.bottom;
            }
            else if (Py.Truthy(r["region"]))
            {
                var reg = r.List("region");
                if (reg.Count != 4) throw new OpError(reg.Count < 4 ? "not enough values to unpack (expected 4, got " + reg.Count + ")" : "too many values to unpack (expected 4)");
                l = (int)ToInt(reg[0]); t = (int)ToInt(reg[1]); rr = (int)ToInt(reg[2]); b = (int)ToInt(reg[3]);
            }
            else { l = 0; t = 0; rr = sw; b = sh; }
            l = Math.Max(l, 0); t = Math.Max(t, 0); rr = Math.Min(rr, sw); b = Math.Min(b, sh);
            int cw = rr - l, ch = b - t;
            if (cw < 1 || ch < 1) return J.Err("leerer Bereich");
            double scale = r.Dbl("scale", 1.0);
            scale = Math.Min(Math.Max(scale, 0.1), 1.0);
            int ow = Math.Max(1, (int)(cw * scale)), oh = Math.Max(1, (int)(ch * scale));
            int stride = ((ow * 3 + 3) / 4) * 4;
            var buf = new byte[stride * oh];
            IntPtr sdc = GetDC(IntPtr.Zero);
            IntPtr mem = IntPtr.Zero, bmp = IntPtr.Zero;
            try
            {
                mem = CreateCompatibleDC(sdc);
                bmp = CreateCompatibleBitmap(sdc, ow, oh);
                IntPtr old = SelectObject(mem, bmp);
                bool ok;
                if (scale >= 1.0) ok = BitBlt(mem, 0, 0, cw, ch, sdc, l, t, 0x40CC0020);
                else
                {
                    SetStretchBltMode(mem, 4);
                    ok = StretchBlt(mem, 0, 0, ow, oh, sdc, l, t, cw, ch, 0x40CC0020);
                }
                SelectObject(mem, old);
                if (!ok) return J.Err("BitBlt fehlgeschlagen");
                var bih = new BITMAPINFOHEADER();
                bih.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
                bih.biWidth = ow; bih.biHeight = -oh; bih.biPlanes = 1; bih.biBitCount = 24;
                int lines = GetDIBits(sdc, bmp, 0, (uint)oh, buf, ref bih, 0);
                if (lines != oh) return J.Err(string.Format("GetDIBits lieferte {0} von {1} Zeilen", lines, oh));
            }
            finally
            {
                if (bmp != IntPtr.Zero) DeleteObject(bmp);
                if (mem != IntPtr.Zero) DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, sdc);
            }
            byte[] payload = ZlibCompress(buf);
            c.SendJson(J.Ok("ready", true, "width", (long)ow, "height", (long)oh, "stride", (long)stride,
                "rect", new List<object> { (long)l, (long)t, (long)rr, (long)b }, "scale", scale, "size", (long)payload.Length));
            using (var h = SHA256.Create())
            {
                for (int i = 0; i < payload.Length; i += Conn.Chunk)
                    c.SendFrame(payload, i, Math.Min(Conn.Chunk, payload.Length - i));
                return J.Ok("sha256", Server.Hex(h.ComputeHash(payload)));
            }
        }

        static string PyRepr(string s)
        {
            string q = s.Contains("'") && !s.Contains("\"") ? "\"" : "'";
            var sb = new StringBuilder(q);
            foreach (char ch in s)
            {
                if (ch == '\\') sb.Append("\\\\");
                else if (ch.ToString() == q) sb.Append("\\" + q);
                else if (ch == '\n') sb.Append("\\n");
                else if (ch == '\r') sb.Append("\\r");
                else if (ch == '\t') sb.Append("\\t");
                else if (ch < 0x20 || ch == 0x7f) sb.Append("\\x" + ((int)ch).ToString("x2"));
                else sb.Append(ch);
            }
            return sb.Append(q).ToString();
        }

        [Op("click")]
        static J Click(Conn c, Req r)
        {
            int x = (int)NeedInt(r, "x"), y = (int)NeedInt(r, "y");
            SetCursorPos(x, y);
            Thread.Sleep(50);
            if (r.Bool("move_only")) return J.Ok();
            string btn = r.Str("button", "left");
            uint down, up;
            if (btn == "left") { down = 2; up = 4; }
            else if (btn == "right") { down = 8; up = 0x10; }
            else if (btn == "middle") { down = 0x20; up = 0x40; }
            else throw new OpError("KeyError", PyRepr(btn));
            int n = r.Bool("double") ? 2 : 1;
            for (int i = 0; i < n; i++)
            {
                mouse_event(down, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(30);
                mouse_event(up, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(60);
            }
            return J.Ok();
        }

        [Op("press")]
        static J Press(Conn c, Req r)
        {
            IntPtr h = P(NeedInt(r, "hwnd"));
            if (!IsWindow(h)) return J.Err("Fenster existiert nicht");
            if (!IsWindowEnabled(h)) return J.Err("Control ist deaktiviert");
            IntPtr parent = GetParent(h);
            int bstyle = GetWindowLongW(h, -16) & 0xF;
            string how;
            if (parent != IntPtr.Zero && (bstyle == 0 || bstyle == 1))
            {
                PostMessageW(parent, 0x0111, new IntPtr(GetDlgCtrlID(h) & 0xFFFF), h);
                how = "WM_COMMAND";
            }
            else
            {
                PostMessageW(h, 0x00F5, IntPtr.Zero, IntPtr.Zero);
                how = "BM_CLICK";
            }
            return J.Ok("how", how);
        }

        [Op("settext")]
        static J SetText(Conn c, Req r)
        {
            string text = r.Need("text");
            IntPtr h = P(NeedInt(r, "hwnd"));
            IntPtr buf = Marshal.StringToHGlobalUni(text);
            try
            {
                long res;
                if (Send(h, 0x000C, IntPtr.Zero, buf, 1000, out res)) return J.Ok();
                return J.Err("WM_SETTEXT ohne Antwort (Fenster hängt?)");
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static void VkFor(string name, out int vk, out int st)
        {
            int v;
            if (VkNames.TryGetValue(name.ToUpperInvariant(), out v)) { vk = v; st = 0; return; }
            if (name.Length == 1)
            {
                short r = VkKeyScanW(name[0]);
                if (r == -1) throw new OpError("Zeichen nicht auf dem Tastaturlayout: " + PyRepr(name));
                vk = r & 0xFF; st = (r >> 8) & 0xFF; return;
            }
            if (name.Length == 2 && char.IsHighSurrogate(name[0]))
                throw new OpError("Zeichen nicht auf dem Tastaturlayout: " + PyRepr(name));
            throw new OpError("unbekannte Taste: " + name);
        }

        static void KeyEv(int vk, bool up)
        {
            uint flags = (uint)((IsExt(vk) ? 1 : 0) | (up ? 2 : 0));
            keybd_event((byte)vk, (byte)(MapVirtualKeyW((uint)vk, 0) & 0xFF), flags, UIntPtr.Zero);
        }

        static void KeyTap(int vk, List<int> mods)
        {
            foreach (int m in mods) KeyEv(m, false);
            KeyEv(vk, false);
            Thread.Sleep(10);
            KeyEv(vk, true);
            for (int i = mods.Count - 1; i >= 0; i--) KeyEv(mods[i], true);
            Thread.Sleep(20);
        }

        [Op("key")]
        static J Key(Conn c, Req r)
        {
            r.Need("keys");
            foreach (object o in r.List("keys"))
            {
                string combo = o as string;
                if (combo == null) throw new OpError("TypeError", "object is not a string");
                var parts = new List<string>();
                if (combo.Length > 1) parts.AddRange(combo.Split('+')); else parts.Add(combo);
                if (combo.EndsWith("++"))
                {
                    parts = new List<string>(combo.Substring(0, combo.Length - 2).Split('+'));
                    parts.Add("+");
                }
                var mods = new List<int>();
                for (int i = 0; i < parts.Count - 1; i++)
                {
                    int m;
                    if (!VkMods.TryGetValue(parts[i].ToUpperInvariant(), out m))
                        throw new OpError("unbekannter Modifier: " + parts[i]);
                    mods.Add(m);
                }
                int vk, st;
                VkFor(parts[parts.Count - 1], out vk, out st);
                KeyTap(vk, mods);
            }
            return J.Ok();
        }

        [Op("type")]
        static J Type(Conn c, Req r)
        {
            string text = r.Need("text");
            for (int i = 0; i < text.Length; i++)
            {
                string ch = text.Substring(i, 1);
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length) { ch = text.Substring(i, 2); i++; }
                if (ch == "\n") { KeyTap(0x0D, new List<int>()); continue; }
                if (ch == "\t") { KeyTap(0x09, new List<int>()); continue; }
                int vk, st;
                VkFor(ch, out vk, out st);
                var mods = new List<int>();
                if ((st & 1) != 0) mods.Add(0x10);
                if ((st & 2) != 0) mods.Add(0x11);
                if ((st & 4) != 0) mods.Add(0x12);
                KeyTap(vk, mods);
            }
            return J.Ok();
        }

        [Op("focus")]
        static J Focus(Conn c, Req r)
        {
            IntPtr h = P(NeedInt(r, "hwnd"));
            if (!IsWindow(h)) return J.Err("Fenster existiert nicht");
            IntPtr fg = GetForegroundWindow();
            uint fgt = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, IntPtr.Zero) : 0;
            uint cur = GetCurrentThreadId();
            if (fgt != 0 && fgt != cur) AttachThreadInput(cur, fgt, true);
            try
            {
                if (IsIconic(h)) ShowWindow(h, 9);
                BringWindowToTop(h);
                SetForegroundWindow(h);
            }
            finally
            {
                if (fgt != 0 && fgt != cur) AttachThreadInput(cur, fgt, false);
            }
            Thread.Sleep(100);
            return J.Ok("foreground", GetForegroundWindow() == h);
        }

        [Op("close")]
        static J Close(Conn c, Req r)
        {
            PostMessageW(P(NeedInt(r, "hwnd")), 0x0010, IntPtr.Zero, IntPtr.Zero);
            return J.Ok();
        }

        [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool repaint);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);

        /// <summary>Fenster auf Bildschirmkoordinaten setzen (minimiert/maximiert vorher wiederherstellen).</summary>
        [Op("winmove")]
        static J WinMove(Conn c, Req r)
        {
            IntPtr h = P(NeedInt(r, "hwnd"));
            if (!IsWindow(h)) return J.Err("Fenster existiert nicht");
            int x = (int)NeedInt(r, "x"), y = (int)NeedInt(r, "y");
            int w = (int)NeedInt(r, "w"), hh = (int)NeedInt(r, "h");
            if (w < 50 || hh < 50) return J.Err("Breite/Höhe mindestens 50");
            if (IsIconic(h) || IsZoomed(h)) ShowWindow(h, 9);
            if (!MoveWindow(h, x, y, w, hh, true)) return J.Err("MoveWindow fehlgeschlagen");
            RECT rc;
            GetWindowRect(h, out rc);
            return J.Ok("rect", new List<object> { (long)rc.left, (long)rc.top, (long)rc.right, (long)rc.bottom });
        }
    }
}
