using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace XpServer
{
    /// <summary>Laufwerke, Images, Treiber, NVIDIA, INI, PE: mount, eject, ripiso, toc, ripcd, vol, pesec, nv, ini, driver.</summary>
    public static class DrivesOps
    {
        static readonly string[] MountSafeFg = { "", "explorer.exe", "python.exe", "cmd.exe", "alcohol.exe", "axcmd.exe", "xpserver.exe" };

        // ---------- P/Invoke ----------

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inb, uint inLen, byte[] outb, uint outLen, out uint got, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFilePointerEx(IntPtr h, long dist, out long newPos, uint method);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadFile(IntPtr h, byte[] buf, uint n, out uint got, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr LoadLibraryA(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr mod, string name);
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, uint flags, uint timeout, out IntPtr res);
        [DllImport("psapi.dll", CharSet = CharSet.Unicode)]
        static extern uint GetModuleBaseNameW(IntPtr proc, IntPtr mod, StringBuilder name, uint size);

        [StructLayout(LayoutKind.Sequential)]
        struct DEVINFO
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevsW(IntPtr guid, string enumerator, IntPtr hwnd, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInfo(IntPtr h, uint index, ref DEVINFO d);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr h, ref DEVINFO d, uint prop, IntPtr type, IntPtr buf, uint size, IntPtr req);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr h);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupCopyOEMInfW(string src, string loc, uint locType, uint copyStyle, StringBuilder dest, uint destSize, IntPtr req, IntPtr comp);
        [DllImport("cfgmgr32.dll")]
        static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
        [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwnd, string hwid, string inf, uint flags, out bool reboot);

        // ---------- Python-nahe Fehlertexte ----------

        /// <summary>repr() eines Strings wie Python.</summary>
        static string PyRepr(string s)
        {
            char q = s.IndexOf('\'') >= 0 && s.IndexOf('"') < 0 ? '"' : '\'';
            var sb = new StringBuilder();
            sb.Append(q);
            foreach (char c in s)
            {
                if (c == q || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20 || c == 0x7f) sb.Append("\\x").Append(((int)c).ToString("x2"));
                else sb.Append(c);
            }
            sb.Append(q);
            return sb.ToString();
        }

        static OpError FileOpErr(string path, Exception e)
        {
            if (e is FileNotFoundException || e is DirectoryNotFoundException)
                return new OpError("FileNotFoundError", "[Errno 2] No such file or directory: " + PyRepr(path));
            if (e is UnauthorizedAccessException || e is IOException)
                return new OpError("PermissionError", "[Errno 13] Permission denied: " + PyRepr(path));
            if (e is ArgumentException || e is NotSupportedException)
                return new OpError("OSError", "[Errno 22] Invalid argument: " + PyRepr(path));
            return new OpError(e.GetType().Name, e.Message);
        }

        static byte[] ReadAll(string path)
        {
            try { return File.ReadAllBytes(path); }
            catch (Exception e) { throw FileOpErr(path, e); }
        }

        static string PyStrip(string s)
        {
            int a = 0, b = s.Length;
            while (a < b && IsPySpace(s[a])) a++;
            while (b > a && IsPySpace(s[b - 1])) b--;
            return s.Substring(a, b - a);
        }

        static bool IsPySpace(char c)
        {
            return (c >= 9 && c <= 13) || (c >= 0x1c && c <= 0x20) || c == 0x85 || c == 0xa0 || c == 0x1680 ||
                   (c >= 0x2000 && c <= 0x200a) || c == 0x2028 || c == 0x2029 || c == 0x202f || c == 0x205f || c == 0x3000;
        }

        static string Hex(byte[] d) { return Server.Hex(d); }

        // ---------- Rohgerät ----------

        class DevErr : OpError { public DevErr(string m) : base("OSError", m) { } }

        class RawDev : IDisposable
        {
            public IntPtr H;
            public RawDev(string drive)
            {
                string path = "\\\\.\\" + drive + ":";
                H = CreateFileW(path, 0x80000000, 0x3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
                if (H == IntPtr.Zero || H == new IntPtr(-1))
                {
                    int err = Marshal.GetLastWin32Error();
                    H = IntPtr.Zero;
                    if (err == 2 || err == 3 || err == 15)
                        throw new OpError("FileNotFoundError", "[Errno 2] No such file or directory: " + PyRepr(path));
                    if (err == 5 || err == 32)
                        throw new OpError("PermissionError", "[Errno 13] Permission denied: " + PyRepr(path));
                    throw new OpError("OSError", "[Errno 22] Invalid argument: " + PyRepr(path));
                }
            }
            public void Seek(long pos)
            {
                long np;
                if (!SetFilePointerEx(H, pos, out np, 0)) throw WinErr();
            }
            public byte[] Read(int n)
            {
                var b = new byte[n];
                uint got;
                if (!ReadFile(H, b, (uint)n, out got, IntPtr.Zero)) throw WinErr();
                if (got == n) return b;
                var r = new byte[got];
                Array.Copy(b, r, got);
                return r;
            }
            public void Dispose() { if (H != IntPtr.Zero) { CloseHandle(H); H = IntPtr.Zero; } }
        }

        static DevErr WinErr()
        {
            int e = Marshal.GetLastWin32Error();
            return new DevErr("[WinError " + e + "] " + new Win32Exception(e).Message);
        }

        static byte[] Ioctl(IntPtr h, uint code, byte[] inb, int outSize)
        {
            var o = new byte[outSize];
            uint got;
            bool ok = DeviceIoControl(h, code, inb, (uint)(inb != null ? inb.Length : 0), o, (uint)outSize, out got, IntPtr.Zero);
            if (!ok) throw new DevErr(string.Format("DeviceIoControl 0x{0:x} Fehler {1}", code, Marshal.GetLastWin32Error()));
            var r = new byte[got];
            Array.Copy(o, r, got);
            return r;
        }

        static byte At(byte[] b, int i)
        {
            if (i < 0 || i >= b.Length) throw new OpError("IndexError", "index out of range");
            return b[i];
        }

        class Track { public int No; public bool Data; public long Lba; public long Sectors; }

        static List<Track> ReadToc(RawDev f)
        {
            var raw = Ioctl(f.H, 0x24000, null, 804);
            int first = At(raw, 2), last = At(raw, 3);
            var ent = new List<Track>();
            for (int i = 0; i < last - first + 2; i++)
            {
                int o = 4 + 8 * i;
                int m = At(raw, o + 5), s = At(raw, o + 6), fr = At(raw, o + 7);
                ent.Add(new Track { No = raw[o + 2], Data = (raw[o + 1] & 0x04) != 0, Lba = (m * 60 + s) * 75 + fr - 150 });
            }
            var tr = new List<Track>();
            for (int i = 0; i + 1 < ent.Count; i++)
                tr.Add(new Track { No = ent[i].No, Data = ent[i].Data, Lba = ent[i].Lba, Sectors = ent[i + 1].Lba - ent[i].Lba });
            return tr;
        }

        static string Drv(Req r)
        {
            return r.Or("drive", "E:").TrimEnd('\\').TrimEnd(':');
        }

        // ---------- Vordergrund, mount ----------

        static void FgProcess(out string exe, out uint pid, out string title)
        {
            exe = ""; pid = 0; title = "";
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return;
            GetWindowThreadProcessId(fg, out pid);
            IntPtr h = OpenProcess(0x0410, false, pid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    var sb = new StringBuilder(260);
                    GetModuleBaseNameW(h, IntPtr.Zero, sb, 260);
                    exe = sb.ToString();
                }
                finally { CloseHandle(h); }
            }
            IntPtr buf = Marshal.AllocHGlobal(802);
            try
            {
                for (int i = 0; i < 802; i++) Marshal.WriteByte(buf, i, 0);
                IntPtr res;
                IntPtr ok = SendMessageTimeoutW(fg, 0x000D, new IntPtr(400), buf, 0x0003, 300, out res);   // WM_GETTEXT
                title = ok != IntPtr.Zero ? Marshal.PtrToStringUni(buf) : "";
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static string MountGuard(Req r)
        {
            if (r.Bool("force")) return null;
            string exe, title; uint pid;
            FgProcess(out exe, out pid, out title);
            if (Array.IndexOf(MountSafeFg, exe.ToLowerInvariant()) < 0)
                return string.Format("Im Vordergrund läuft {0} (pid {1}, \"{2}\"): spielt gerade jemand? Nichts gemountet. --force erzwingt es.",
                    exe, pid, title);
            return null;
        }

        static string Ax(string args)
        {
            string axcmd = Cfg.Need("alcohol_axcmd", "Pfad zu AxCmd.exe (Alcohol 120%)");
            var psi = new System.Diagnostics.ProcessStartInfo(axcmd, args);
            var res = Proc.Run(psi, 60, true);
            if (res.TimedOut) throw new OpError("TimeoutExpired", "Command '\"" + axcmd + "\" " + args + "' timed out after 60 seconds");
            return Proc.Oem.GetString(res.Out).Trim();
        }

        [Op("mount")]
        static J Mount(Conn c, Req r)
        {
            string drive = (r.Or("drive", null) ?? Cfg.Need("image_drive", "virtuelles Alcohol-Laufwerk, z. B. \"G:\"")).TrimEnd('\\');
            Cfg.Need("alcohol_axcmd", "Pfad zu AxCmd.exe (Alcohol 120%)");
            string stop = MountGuard(r);
            if (stop != null) return J.Err(stop);
            string before = Common.VolLabel(drive);
            Ax(drive + " /U");
            string label = null;
            if (r.Bool("image"))
            {
                string image = r.Str("image");
                Ax(drive + " /M:\"" + image + "\"");
                for (int i = 0; i < 40; i++)
                {
                    label = Common.VolLabel(drive);
                    if (label != null) break;
                    Thread.Sleep(500);
                }
                if (label == null) return J.Err("Image hängt nicht auf " + drive + ": " + image);
            }
            return J.Ok("drive", drive, "before", before, "label", label);
        }

        [Op("eject")]
        static J Eject(Conn c, Req r)
        {
            string drive = Common.CdDrive(r);
            string stop = Common.RipGuard(r, drive);
            if (stop != null) return J.Err(stop);
            IntPtr h = CreateFileW("\\\\.\\" + drive, 0x80000000, 0x3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return J.Err("Laufwerk " + drive + " nicht zu öffnen");
            bool ok; int err = 0;
            try
            {
                uint n;
                ok = DeviceIoControl(h, 0x2D4808, null, 0, null, 0, out n, IntPtr.Zero);   // IOCTL_STORAGE_EJECT_MEDIA
                if (!ok) err = Marshal.GetLastWin32Error();
            }
            finally { CloseHandle(h); }
            return J.O("ok", ok, "error", ok ? null : "Auswerfen fehlgeschlagen (Fehler " + err + ")");
        }

        // ---------- ripiso / toc / ripcd ----------

        static string AsciiReplace(byte[] d, int off, int len)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < len; i++) sb.Append(d[off + i] < 128 ? (char)d[off + i] : '\uFFFD');
            return sb.ToString();
        }

        static byte[] ReadSectors(RawDev src, long first, int count, int sec)
        {
            src.Seek(first * sec);
            var data = src.Read(count * sec);
            if (data.Length != count * sec) throw new DevErr("kurz gelesen");
            return data;
        }

        static void Append(ref byte[] a, byte[] b)
        {
            var n = new byte[a.Length + b.Length];
            Array.Copy(a, n, a.Length);
            Array.Copy(b, 0, n, a.Length, b.Length);
            a = n;
        }

        static FileStream OpenWrite(string path)
        {
            try { return new FileStream(path, FileMode.Create, FileAccess.Write); }
            catch (Exception e) { throw FileOpErr(path, e); }
        }

        [Op("ripiso")]
        static J RipIso(Conn c, Req r)
        {
            string drive = Common.CdDrive(r).TrimEnd(':');
            string stop = Common.RipGuard(r, drive);
            if (stop != null) return J.Err(stop);
            string target = r.Need("target");
            const int sec = 2048, tail = 150;
            string parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent)) Directory.CreateDirectory(parent);
            var start = DateTime.UtcNow;
            using (var src = new RawDev(drive))
            {
                src.Seek(16L * sec);
                var pvd = src.Read(sec);
                if (pvd.Length < 6 || pvd[1] != 'C' || pvd[2] != 'D' || pvd[3] != '0' || pvd[4] != '0' || pvd[5] != '1')
                    return J.Err("kein ISO9660-Datenträger in " + drive + ":");
                if (pvd.Length < 84) throw new OpError("error", "unpack_from requires a buffer of at least 84 bytes");
                string label = PyStrip(AsciiReplace(pvd, 40, 32));
                long sectors = BitConverter.ToUInt32(pvd, 80);
                var md5 = MD5.Create();
                long done = 0;
                var padded = new List<long>();
                using (var dst = OpenWrite(target))
                {
                    while (done < sectors)
                    {
                        int n = (int)Math.Min(32, sectors - done);
                        byte[] data;
                        try { data = ReadSectors(src, done, n, sec); }
                        catch (DevErr)
                        {
                            data = new byte[0];
                            for (long s = done; s < done + n; s++)
                            {
                                try { Append(ref data, ReadSectors(src, s, 1, sec)); }
                                catch (DevErr)
                                {
                                    if (sectors - s > tail)
                                        return J.Err("Lesefehler bei Sektor " + s + " (nicht im Auslauf, Disc prüfen oder Kopierschutz: dann Alcohol MDF/MDS)");
                                    padded.Add(s);
                                    Append(ref data, new byte[sec]);
                                }
                            }
                        }
                        dst.Write(data, 0, data.Length);
                        md5.TransformBlock(data, 0, data.Length, null, 0);
                        done += n;
                    }
                }
                md5.TransformFinalBlock(new byte[0], 0, 0);
                return J.Ok("label", label, "sectors", done, "md5", Hex(md5.Hash),
                    "seconds", (long)(DateTime.UtcNow - start).TotalSeconds,
                    "padded", padded.Count > 0 ? new List<object> { padded[0], padded[padded.Count - 1], (long)padded.Count } : null,
                    "target", target);
            }
        }

        [Op("toc")]
        static J Toc(Conn c, Req r)
        {
            string drive = Drv(r);
            string stop = Common.RipGuard(r, drive);
            if (stop != null) return J.Err(stop);
            using (var f = new RawDev(drive))
            {
                var tr = ReadToc(f);
                var l = new List<object>();
                foreach (var t in tr) l.Add(J.O("track", (long)t.No, "data", t.Data, "lba", t.Lba, "sectors", t.Sectors));
                return J.Ok("label", Common.VolLabel(drive + ":"), "tracks", l);
            }
        }

        static byte[] RawRead(long offset, int count)
        {
            var b = new byte[16];
            Array.Copy(BitConverter.GetBytes(offset), 0, b, 0, 8);
            Array.Copy(BitConverter.GetBytes((uint)count), 0, b, 8, 4);
            Array.Copy(BitConverter.GetBytes(2u), 0, b, 12, 4);
            return b;
        }

        [Op("ripcd")]
        static J RipCd(Conn c, Req r)
        {
            string drive = Drv(r);
            string stop = Common.RipGuard(r, drive);
            if (stop != null) return J.Err(stop);
            string folder = r.Need("folder"), name = r.Need("name");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            var start = DateTime.UtcNow;
            var cue = new List<string> { "REM xpctl ripcd" };
            var report = new List<object>();
            using (var f = new RawDev(drive))
            {
                var tracks = ReadToc(f);
                foreach (var t in tracks)
                {
                    int n = t.No;
                    var md5 = MD5.Create();
                    var bad = new List<long>();
                    string fn;
                    if (t.Data)
                    {
                        fn = string.Format("{0} {1:00}.iso", name, n);
                        using (var dst = OpenWrite(Path.Combine(folder, fn)))
                        {
                            long pos = 0;
                            while (pos < t.Sectors)
                            {
                                int k = (int)Math.Min(32, t.Sectors - pos);
                                byte[] data;
                                try { data = ReadSectors(f, t.Lba + pos, k, 2048); }
                                catch (DevErr)
                                {
                                    data = new byte[0];
                                    for (int s = 0; s < k; s++)
                                    {
                                        byte[] one;
                                        try { one = ReadSectors(f, t.Lba + pos + s, 1, 2048); }
                                        catch (DevErr) { one = new byte[2048]; bad.Add(t.Lba + pos + s); }
                                        Append(ref data, one);
                                    }
                                }
                                dst.Write(data, 0, data.Length);
                                md5.TransformBlock(data, 0, data.Length, null, 0);
                                pos += k;
                            }
                        }
                        cue.Add("FILE \"" + fn + "\" BINARY");
                        cue.Add(string.Format("  TRACK {0:00} MODE1/2048", n));
                        cue.Add("    INDEX 01 00:00:00");
                    }
                    else
                    {
                        fn = string.Format("{0} {1:00}.bin", name, n);
                        using (var dst = OpenWrite(Path.Combine(folder, fn)))
                        {
                            long pos = 0;
                            while (pos < t.Sectors)
                            {
                                int k = (int)Math.Min(26, t.Sectors - pos);
                                byte[] data;
                                try
                                {
                                    data = Ioctl(f.H, 0x2403E, RawRead((t.Lba + pos) * 2048, k), k * 2352);
                                    if (data.Length != k * 2352) throw new DevErr("kurz");
                                }
                                catch (DevErr)
                                {
                                    data = new byte[0];
                                    for (int s = 0; s < k; s++)
                                    {
                                        byte[] one;
                                        try { one = Ioctl(f.H, 0x2403E, RawRead((t.Lba + pos + s) * 2048, 1), 2352); }
                                        catch (DevErr) { one = new byte[2352]; bad.Add(t.Lba + pos + s); }
                                        Append(ref data, one);
                                    }
                                }
                                dst.Write(data, 0, data.Length);
                                md5.TransformBlock(data, 0, data.Length, null, 0);
                                pos += k;
                            }
                        }
                        cue.Add("FILE \"" + fn + "\" BINARY");
                        cue.Add(string.Format("  TRACK {0:00} AUDIO", n));
                        cue.Add("    INDEX 01 00:00:00");
                    }
                    md5.TransformFinalBlock(new byte[0], 0, 0);
                    report.Add(J.O("track", (long)n, "data", t.Data, "sectors", t.Sectors, "file", fn, "md5", Hex(md5.Hash),
                        "bad", (long)bad.Count, "bad_range", bad.Count > 0 ? new List<object> { bad[0], bad[bad.Count - 1] } : null));
                }
            }
            string cuefile = Path.Combine(folder, name + ".cue");
            // Bewusst anders als der Python-Server: der schrieb "\r\r\n" (Textmodus + \r\n), hier sauberes CRLF.
            string text = string.Join("\n", cue.ToArray()) + "\n";
            using (var cf = OpenWrite(cuefile))
            {
                var b = Encoding.Default.GetBytes(text.Replace("\n", "\r\n"));
                cf.Write(b, 0, b.Length);
            }
            return J.Ok("label", Common.VolLabel(drive + ":"), "cue", cuefile, "tracks", report,
                "seconds", (long)(DateTime.UtcNow - start).TotalSeconds);
        }

        // ---------- NVIDIA (NVAPI DRS) ----------

        class NvDef
        {
            public string Name; public uint Id; public string[] Keys; public uint[] Vals;
        }

        static readonly NvDef[] NvSettings =
        {
            new NvDef { Name = "vsync", Id = 0x00A879CF, Keys = new[] { "app", "off", "on" }, Vals = new uint[] { 0x60925292, 0x08416747, 0x47814940 } },
            new NvDef { Name = "triplebuffer", Id = 0x20FDD1F9, Keys = new[] { "off", "on" }, Vals = new uint[] { 0, 1 } },
            new NvDef { Name = "prerender", Id = 0x007BA09E, Keys = null, Vals = null },
            // IDs/Werte aus NVIDIA NvApiDriverSettings.h: AA_MODE_SELECTOR/METHOD, ANISO_MODE_SELECTOR/LEVEL
            new NvDef { Name = "aa", Id = 0x107EFC5B, Keys = new[] { "app", "override", "enhance" }, Vals = new uint[] { 0, 1, 2 } },
            new NvDef { Name = "aamethod", Id = 0x10D773D2, Keys = new[] { "none", "2x", "2xq", "4x", "8x", "16x" }, Vals = new uint[] { 0x0, 0xE, 0xF, 0x10, 0x25, 0x1C } },
            new NvDef { Name = "af", Id = 0x10D2BB16, Keys = new[] { "app", "user" }, Vals = new uint[] { 0, 1 } },
            new NvDef { Name = "aflevel", Id = 0x101E61A9, Keys = null, Vals = null },
        };

        static NvDef NvFind(string name)
        {
            foreach (var d in NvSettings) if (d.Name == name) return d;
            throw new OpError("KeyError", PyRepr(name));
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr NvQuery(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NvInit();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NvCreate(out IntPtr sess);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NvH1(IntPtr a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NvBase(IntPtr a, out IntPtr prof);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NvGet(IntPtr a, IntPtr b, uint id, IntPtr st);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NvSet(IntPtr a, IntPtr b, IntPtr st);

        static NvQuery nvq;

        static T NvFn<T>(uint iid) where T : class
        {
            IntPtr p = nvq(iid);
            if (p == IntPtr.Zero) throw new OpError("OSError", string.Format("NVAPI-Funktion 0x{0:X8} fehlt im Treiber", iid));
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        // NVDRS_SETTING: version @0, name[2048] wchar @4, id @4100, type @4104, ..., current @8220, Gesamtgröße 12320
        const int NvSize = 12320;

        static IntPtr NvSetting(uint id, bool setCurrent, uint cur)
        {
            IntPtr st = Marshal.AllocHGlobal(NvSize);
            for (int i = 0; i < NvSize; i += 4) Marshal.WriteInt32(st, i, 0);
            Marshal.WriteInt32(st, 0, unchecked((int)((uint)NvSize | (1u << 16))));
            if (setCurrent)
            {
                Marshal.WriteInt32(st, 4100, unchecked((int)id));
                Marshal.WriteInt32(st, 4104, 0);
                Marshal.WriteInt32(st, 8220, unchecked((int)cur));
            }
            return st;
        }

        static long PyInt(object v)
        {
            if (v is long) return (long)v;
            if (v is bool) return (bool)v ? 1 : 0;
            if (v is double) return (long)(double)v;
            string s = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            long n;
            if (!long.TryParse(PyStrip(s), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out n))
                throw new OpError("invalid literal for int() with base 10: " + PyRepr(s));
            return n;
        }

        [Op("nv")]
        static J Nv(Conn c, Req r)
        {
            IntPtr mod = LoadLibraryA("nvapi.dll");
            if (mod == IntPtr.Zero)
            {
                int e = Marshal.GetLastWin32Error();
                throw new OpError("OSError", "[WinError " + e + "] " + new Win32Exception(e).Message);
            }
            IntPtr qp = GetProcAddress(mod, "nvapi_QueryInterface");
            if (qp == IntPtr.Zero) throw new OpError("AttributeError", "function 'nvapi_QueryInterface' not found");
            nvq = (NvQuery)Marshal.GetDelegateForFunctionPointer(qp, typeof(NvQuery));
            if (NvFn<NvInit>(0x0150E828)() != 0) throw new OpError("OSError", "NvAPI_Initialize fehlgeschlagen");
            var create = NvFn<NvCreate>(0x0694D52E);
            var load = NvFn<NvH1>(0x375DBD6B);
            var bas = NvFn<NvBase>(0xDA8466A0);
            var get = NvFn<NvGet>(0x73BF8338);
            var setf = NvFn<NvSet>(0x577DD202);
            var save = NvFn<NvH1>(0xFCBC7E14);
            var destroy = NvFn<NvH1>(0xDAD9CFF8);

            var names = new List<string>();
            var rawNames = r.List("names");
            if (rawNames.Count > 0) foreach (var n in rawNames) names.Add(Convert.ToString(n));
            else foreach (var d in Py.SortBy(NvSettings, x => x.Name)) names.Add(d.Name);

            IntPtr sess, prof;
            if (create(out sess) != 0) return J.Err("NvAPI_DRS_CreateSession fehlgeschlagen");
            try
            {
                int rc = load(sess);
                if (rc != 0) return J.Err("NvAPI_DRS_LoadSettings: Fehler " + rc);
                rc = bas(sess, out prof);
                if (rc != 0) return J.Err("NvAPI_DRS_GetBaseProfile: Fehler " + rc);
                if (r.Bool("set"))
                {
                    var sv = r.List("set");
                    if (sv.Count != 2) throw new OpError(sv.Count > 2 ? "too many values to unpack (expected 2)" :
                        "need more than " + sv.Count + " value" + (sv.Count == 1 ? "" : "s") + " to unpack");
                    string name = Convert.ToString(sv[0]);
                    var def = NvFind(name);
                    uint v;
                    if (def.Keys != null)
                    {
                        string vs = Convert.ToString(sv[1]);
                        int ix = Array.IndexOf(def.Keys, vs);
                        if (!(sv[1] is string) || ix < 0) throw new OpError("KeyError", sv[1] is string ? PyRepr(vs) : Py.Repr(sv[1]));
                        v = def.Vals[ix];
                    }
                    else v = unchecked((uint)PyInt(sv[1]));
                    IntPtr st = NvSetting(def.Id, true, v);
                    try
                    {
                        rc = setf(sess, prof, st);
                        if (rc != 0) return J.Err("SetSetting " + name + ": Fehler " + rc);
                    }
                    finally { Marshal.FreeHGlobal(st); }
                    rc = save(sess);
                    if (rc != 0) return J.Err("SaveSettings: Fehler " + rc);
                    names = new List<string> { name };
                }
                var outJ = new J();
                foreach (var name in names)
                {
                    var def = NvFind(name);
                    IntPtr st = NvSetting(0, false, 0);
                    try
                    {
                        rc = get(sess, prof, def.Id, st);
                        if (rc != 0)
                        {
                            outJ[name] = rc == -160 ? "Treiber-Standard (nicht gesetzt)" : "Fehler " + rc;
                            continue;
                        }
                        uint cur = unchecked((uint)Marshal.ReadInt32(st, 8220));
                        if (def.Keys == null) outJ[name] = (long)cur;
                        else
                        {
                            string s = null;
                            for (int i = 0; i < def.Vals.Length; i++) if (def.Vals[i] == cur) s = def.Keys[i];
                            outJ[name] = s ?? string.Format("0x{0:X8}", cur);
                        }
                    }
                    finally { Marshal.FreeHGlobal(st); }
                }
                return J.Ok("settings", outJ);
            }
            finally { destroy(sess); }
        }

        // ---------- ini ----------

        static List<string> SplitLines(string s)
        {
            var l = new List<string>();
            int a = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == '\r' && i + 1 < s.Length && s[i + 1] == '\n') { l.Add(s.Substring(a, i + 2 - a)); a = i + 2; i++; }
                else if (ch == '\n' || ch == '\r' || ch == 0x0b || ch == 0x0c || (ch >= 0x1c && ch <= 0x1e) || ch == 0x85 || ch == 0x2028 || ch == 0x2029)
                { l.Add(s.Substring(a, i + 1 - a)); a = i + 1; }
            }
            if (a < s.Length) l.Add(s.Substring(a));
            return l;
        }

        [Op("ini")]
        static J Ini(Conn c, Req r)
        {
            string path = r.Need("path");
            string sec = r.Or("section", "").ToLowerInvariant();
            string rawKey = r.Need("key");
            string key = rawKey.ToLowerInvariant();
            var latin1 = Encoding.GetEncoding(28591);
            var lines = SplitLines(latin1.GetString(ReadAll(path)));
            string cur = "";
            int hit = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = PyStrip(lines[i]);
                if (t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal))
                {
                    cur = PyStrip(t.Length >= 2 ? t.Substring(1, t.Length - 2) : "").ToLowerInvariant();
                    continue;
                }
                if ((sec == "" || cur == sec) && t.IndexOf('=') >= 0 && !t.StartsWith("#", StringComparison.Ordinal) && !t.StartsWith(";", StringComparison.Ordinal))
                {
                    if (PyStrip(t.Substring(0, t.IndexOf('='))).ToLowerInvariant() == key) { hit = i; break; }
                }
            }
            string old = null;
            if (hit >= 0)
            {
                string l = lines[hit];
                old = PyStrip(l.Substring(l.IndexOf('=') + 1));
            }
            if (!r.Has("value")) return J.Ok("old", old, "changed", false);
            string value = r.Str("value");
            if (old == value) return J.Ok("old", old, "changed", false);
            bool crlf = false;
            foreach (var l in lines) if (l.EndsWith("\r\n", StringComparison.Ordinal)) { crlf = true; break; }
            string nl = crlf ? "\r\n" : "\n";
            string nw = rawKey + "=" + value + nl;
            if (hit >= 0) lines[hit] = nw;
            else if (sec != "")
            {
                int idx = -1;
                for (int i = 0; i < lines.Count; i++)
                    if (PyStrip(lines[i]).ToLowerInvariant() == "[" + sec + "]") { idx = i; break; }
                if (idx < 0) return J.Err("Abschnitt [" + r.Str("section") + "] fehlt in " + path);
                lines.Insert(idx + 1, nw);
            }
            else lines.Add(nw);
            string bak = path + ".xpctl-bak";
            if (!File.Exists(bak)) File.Copy(path, bak);
            File.WriteAllBytes(path, latin1.GetBytes(string.Join("", lines.ToArray())));
            return J.Ok("old", old, "changed", true, "backup", bak);
        }

        // ---------- pesec, vol ----------

        static long U(byte[] d, long off, int size)
        {
            // Python 3.4 (32 Bit): Offset über ssize_t -> OverflowError, zu kurzer Puffer nennt nur die Strukturgröße
            if (off > int.MaxValue) throw new OpError("OverflowError", "Python int too large to convert to C ssize_t");
            if (off + size > d.Length)
                throw new OpError("error", "unpack_from requires a buffer of at least " + size + " bytes");
            return size == 4 ? BitConverter.ToUInt32(d, (int)off) : BitConverter.ToUInt16(d, (int)off);
        }

        [Op("pesec")]
        static J PeSec(Conn c, Req r)
        {
            var o = new J();
            r.Need("paths");
            var latin1 = Encoding.GetEncoding(28591);
            foreach (var po in r.List("paths"))
            {
                string p = Convert.ToString(po);
                try
                {
                    byte[] d = new byte[4096];
                    int got = 0;
                    try
                    {
                        using (var f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            int n;
                            while (got < d.Length && (n = f.Read(d, got, d.Length - got)) > 0) got += n;
                        }
                    }
                    catch (Exception e) { throw FileOpErr(p, e); }
                    if (got < d.Length) { var s = new byte[got]; Array.Copy(d, s, got); d = s; }
                    long po2 = U(d, 0x3C, 4);
                    long n2 = U(d, po2 + 6, 2);
                    long so = po2 + 24 + U(d, po2 + 20, 2);
                    var names = new List<object>();
                    for (long i = 0; i < n2; i++)
                    {
                        long a = so + 40 * i, b = Math.Min(a + 8, d.Length);
                        if (a >= d.Length) { names.Add(""); continue; }
                        while (b > a && d[b - 1] == 0) b--;
                        names.Add(latin1.GetString(d, (int)a, (int)(b - a)));
                    }
                    o[p] = names;
                }
                catch (Exception e) { o[p] = "Fehler: " + e.Message; }
            }
            return J.Ok("sections", o);
        }

        [Op("vol")]
        static J Vol(Conn c, Req r)
        {
            var drives = new List<string>();
            foreach (var d in r.List("drives")) drives.Add(Convert.ToString(d));
            if (drives.Count == 0)
            {
                foreach (var k in new[] { "cd_drive", "image_drive" })
                    if (Py.Truthy(Cfg.Get(k))) drives.Add(Cfg.Str(k));
            }
            if (drives.Count == 0) drives.Add(Common.CdDrive(new Req(new J())));
            var o = new J();
            foreach (var d in drives)
                o[d] = Common.RipGuard(r, d) != null ? "(nicht abgefragt: Alcohol-Assistent offen, --force)" : Common.VolLabel(d);
            return J.Ok("drives", o);
        }

        // ---------- driver ----------

        static List<string> DevProp(IntPtr h, ref DEVINFO d, uint code)
        {
            IntPtr buf = Marshal.AllocHGlobal(4096);
            try
            {
                for (int i = 0; i < 4096; i += 4) Marshal.WriteInt32(buf, i, 0);
                var l = new List<string>();
                if (!SetupDiGetDeviceRegistryPropertyW(h, ref d, code, IntPtr.Zero, buf, 4096, IntPtr.Zero)) return l;
                string all = Marshal.PtrToStringUni(buf, 2048);
                foreach (var s in all.Split('\0')) if (s.Length > 0) l.Add(s);
                return l;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static string First(List<string> l) { return l.Count > 0 ? l[0] : ""; }

        class Dev
        {
            public List<string> Ids; public string Desc, Service, Driver; public long Problem; public bool HasStatus, Started;
        }

        static List<Dev> PresentDevices(string filt)
        {
            IntPtr h = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, 0x2 | 0x4);   // DIGCF_PRESENT | DIGCF_ALLCLASSES
            if (h == IntPtr.Zero || h == new IntPtr(-1)) throw new OpError("OSError", "SetupDiGetClassDevs fehlgeschlagen");
            var outL = new List<Dev>();
            try
            {
                for (uint i = 0; ; i++)
                {
                    var d = new DEVINFO();
                    d.cbSize = (uint)Marshal.SizeOf(typeof(DEVINFO));
                    if (!SetupDiEnumDeviceInfo(h, i, ref d)) break;
                    var ids = DevProp(h, ref d, 1);
                    ids.AddRange(DevProp(h, ref d, 2));   // SPDRP_HARDWAREID, SPDRP_COMPATIBLEIDS
                    var dl = DevProp(h, ref d, 12);
                    if (dl.Count == 0) dl = DevProp(h, ref d, 0);   // FRIENDLYNAME, DEVICEDESC
                    string desc = First(dl);
                    if (!string.IsNullOrEmpty(filt))
                    {
                        string f = filt.ToLowerInvariant();
                        bool any = desc.ToLowerInvariant().Contains(f);
                        foreach (var s in ids) if (s.ToLowerInvariant().Contains(f)) any = true;
                        if (!any) continue;
                    }
                    uint st, prob;
                    uint cr = CM_Get_DevNode_Status(out st, out prob, d.DevInst, 0);
                    outL.Add(new Dev
                    {
                        Ids = ids, Desc = desc, Service = First(DevProp(h, ref d, 4)), Driver = First(DevProp(h, ref d, 9)),
                        HasStatus = cr == 0, Problem = prob, Started = (st & 0x8) != 0,   // DN_STARTED
                    });
                }
            }
            finally { SetupDiDestroyDeviceInfoList(h); }
            return outL;
        }

        static J DevJ(Dev d)
        {
            var ids = new List<object>();
            foreach (var s in d.Ids) ids.Add(s);
            return J.O("ids", ids, "desc", d.Desc, "service", d.Service, "driver", d.Driver,
                "problem", d.HasStatus ? (object)d.Problem : null, "started", d.HasStatus ? (object)d.Started : null);
        }

        [Op("driver")]
        static J Driver(Conn c, Req r)
        {
            if (r.Str("action") == "list")
            {
                var l = new List<object>();
                foreach (var d in PresentDevices(r.Or("filter", null))) l.Add(DevJ(d));
                return J.Ok("devices", l);
            }
            string inf = r.Need("inf");
            if (!File.Exists(inf)) return J.Err("INF fehlt: " + inf);
            string text = Encoding.GetEncoding(28591).GetString(ReadAll(inf)).ToUpperInvariant();
            var hits = new List<string>();
            foreach (var dev in PresentDevices(null))
            {
                string hid = null;
                foreach (var s in dev.Ids)
                    if (text.Contains(s.ToUpperInvariant()) && s.IndexOf('\\') >= 0) { hid = s; break; }
                if (hid != null && !hits.Contains(hid)) hits.Add(hid);
            }
            if (r.Bool("hwid")) hits = new List<string> { r.Str("hwid") };
            var results = new List<object>();
            var errs = new List<string>();
            bool good = true;
            foreach (var hid in hits)
            {
                bool reboot;
                bool ok = UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hid, inf, 0x1, out reboot);   // INSTALLFLAG_FORCE
                long err = ok ? 0 : (long)(uint)Marshal.GetLastWin32Error();
                results.Add(J.O("hwid", hid, "ok", ok, "reboot", reboot, "error", ok ? null : (object)err));
                if (!ok) { good = false; errs.Add(hid + " Fehler 0x" + err.ToString("x")); }
            }
            if (results.Count > 0)
                return J.O("ok", good, "devices", results,
                    "error", good ? null : "Installation fehlgeschlagen: " + string.Join(", ", errs.ToArray()));
            var buf = new StringBuilder(260);
            bool cok = SetupCopyOEMInfW(inf, null, 1, 0, buf, 260, IntPtr.Zero, IntPtr.Zero);   // SPOST_PATH
            long cerr = cok ? 0 : (long)(uint)Marshal.GetLastWin32Error();
            var oem = J.O("ok", cok, "path", buf.ToString(), "error", cok ? null : (object)cerr);
            return J.O("ok", cok, "devices", new List<object>(), "oeminf", oem,
                "error", cok ? null : "SetupCopyOEMInf Fehler " + cerr);
        }
    }
}
