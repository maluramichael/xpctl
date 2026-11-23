using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace XpServer
{
    // ---------- COM: IShellLinkW (vtable-Reihenfolge laut shobjidl.h) ----------

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLinkCoClass { }

    /// <summary>Eine .lnk-Datei lesen/schreiben (ShellLink in xpserver.py).</summary>
    sealed class ShellLink
    {
        public readonly string Path;
        IShellLinkW sl;
        IPersistFile pf;

        public ShellLink(string path)
        {
            Path = path;
            sl = (IShellLinkW)new ShellLinkCoClass();
            pf = (IPersistFile)sl;
            if (Shell.Exists(path)) pf.Load(path, 0);
        }

        string Str(int which)
        {
            var sb = new StringBuilder(1024);
            if (which == 0) sl.GetArguments(sb, 1024);
            else if (which == 1) sl.GetWorkingDirectory(sb, 1024);
            else sl.GetDescription(sb, 1024);
            return sb.ToString();
        }

        public J Read()
        {
            var buf = new StringBuilder(1024);
            sl.GetPath(buf, 1024, IntPtr.Zero, 4);
            var ibuf = new StringBuilder(1024);
            int idx;
            sl.GetIconLocation(ibuf, 1024, out idx);
            int show;
            sl.GetShowCmd(out show);
            return J.O("path", Path, "target", buf.ToString(), "args", Str(0), "dir", Str(1),
                "icon", ibuf.ToString() + "," + idx, "show", (long)show, "desc", Str(2));
        }

        public void Set(string target, string args, string wdir, string icon, long? show)
        {
            if (target != null) sl.SetPath(target);
            if (args != null) sl.SetArguments(args);
            if (wdir != null) sl.SetWorkingDirectory(wdir);
            if (icon != null)
            {
                string f; int i;
                Shell.SplitIcon(icon, out f, out i);
                sl.SetIconLocation(f, i);
            }
            if (show != null) sl.SetShowCmd((int)show.Value);
        }

        public void Save() { pf.Save(Path, true); }

        public void Close()
        {
            if (sl != null) Marshal.FinalReleaseComObject(sl);
            sl = null; pf = null;
        }
    }

    /// <summary>Geordnetes {Name: Pfad} wie ein Python-dict (erste Einfügeposition bleibt bei Überschreiben).</summary>
    sealed class LinkMap
    {
        public readonly List<string> Keys = new List<string>();
        readonly Dictionary<string, string> d = new Dictionary<string, string>();
        public void Set(string k, string v) { if (!d.ContainsKey(k)) Keys.Add(k); d[k] = v; }
        public string this[string k] { get { return d[k]; } }
    }

    /// <summary>Verknüpfungen, Desktop-Gruppen, Icon-Positionen, Datei-/Spiele-Versionen.</summary>
    public static class Shell
    {
        static readonly string[] DeskSystem = { "my documents", "my computer", "recycle bin", "eigene dateien",
            "arbeitsplatz", "papierkorb", "my network places", "netzwerkumgebung" };

        // ---------- P/Invoke ----------

        [DllImport("shell32.dll")]
        static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern uint GetFileAttributesW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool SetFileAttributesW(string path, uint attr);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindowW(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")]
        static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);

        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        static extern IntPtr FindWindowA(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Ansi)]
        static extern IntPtr FindWindowExA(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessageA(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")]
        static extern int GetWindowLongA(IntPtr h, int idx);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")]
        static extern bool SystemParametersInfoA(uint action, uint param, [In, Out] int[] pv, uint winIni);

        [DllImport("kernel32.dll")]
        static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")]
        static extern IntPtr VirtualAllocEx(IntPtr proc, IntPtr addr, uint size, uint type, uint protect);
        [DllImport("kernel32.dll")]
        static extern bool VirtualFreeEx(IntPtr proc, IntPtr addr, uint size, uint type);
        [DllImport("kernel32.dll")]
        static extern bool ReadProcessMemory(IntPtr proc, IntPtr addr, byte[] buf, int size, out IntPtr read);
        [DllImport("kernel32.dll")]
        static extern bool WriteProcessMemory(IntPtr proc, IntPtr addr, byte[] buf, int size, out IntPtr written);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        [DllImport("version.dll", CharSet = CharSet.Unicode)]
        static extern uint GetFileVersionInfoSizeW(string path, IntPtr handle);
        [DllImport("version.dll", CharSet = CharSet.Unicode)]
        static extern bool GetFileVersionInfoW(string path, uint handle, uint len, IntPtr data);
        [DllImport("version.dll", CharSet = CharSet.Unicode)]
        static extern bool VerQueryValueW(IntPtr block, string sub, out IntPtr buf, out uint len);

        // ---------- Python-os.path-Nachbau (ntpath) ----------

        public static bool Exists(string p) { return File.Exists(p) || Directory.Exists(p); }

        static void SplitDrive(string p, out string drive, out string rest)
        {
            if (p.Length > 1)
            {
                string n = p.Replace('/', '\\');
                if (n.StartsWith("\\\\") && (n.Length < 3 || n[2] != '\\'))
                {
                    int index = n.IndexOf('\\', 2);
                    if (index == -1) { drive = ""; rest = p; return; }
                    int index2 = n.IndexOf('\\', index + 1);
                    if (index2 == index + 1) { drive = ""; rest = p; return; }
                    if (index2 == -1) index2 = p.Length;
                    drive = p.Substring(0, index2); rest = p.Substring(index2); return;
                }
                if (n[1] == ':') { drive = p.Substring(0, 2); rest = p.Substring(2); return; }
            }
            drive = ""; rest = p;
        }

        static void PySplit(string p, out string head, out string tail)
        {
            string d, r;
            SplitDrive(p, out d, out r);
            int i = r.Length;
            while (i > 0 && r[i - 1] != '\\' && r[i - 1] != '/') i--;
            string h = r.Substring(0, i);
            tail = r.Substring(i);
            string h2 = h.TrimEnd('\\', '/');
            head = d + (h2.Length > 0 ? h2 : h);
        }

        public static string DirName(string p) { string h, t; PySplit(p, out h, out t); return h; }
        public static string BaseName(string p) { string h, t; PySplit(p, out h, out t); return t; }

        static string NormPath(string path)
        {
            path = path.Replace('/', '\\');
            string prefix, rest;
            SplitDrive(path, out prefix, out rest);
            path = rest;
            if (prefix == "")
            {
                while (path.StartsWith("\\")) { prefix += "\\"; path = path.Substring(1); }
            }
            else if (path.StartsWith("\\"))
            {
                prefix += "\\";
                path = path.TrimStart('\\');
            }
            var comps = new List<string>(path.Split('\\'));
            int i = 0;
            while (i < comps.Count)
            {
                if (comps[i] == "." || comps[i] == "") comps.RemoveAt(i);
                else if (comps[i] == "..")
                {
                    if (i > 0 && comps[i - 1] != "..") { comps.RemoveRange(i - 1, 2); i--; }
                    else if (i == 0 && prefix.EndsWith("\\")) comps.RemoveAt(i);
                    else i++;
                }
                else i++;
            }
            if (prefix == "" && comps.Count == 0) comps.Add(".");
            return prefix + string.Join("\\", comps.ToArray());
        }

        static string Join(string a, string b) { return System.IO.Path.Combine(a, b); }

        static string Low(string s) { return s.ToLowerInvariant(); }

        static object Key(Req r, string k)
        {
            if (!r.Raw.ContainsKey(k)) throw new OpError("KeyError", "'" + k + "'");
            return r.Raw[k];
        }

        static string KeyStr(Req r, string k) { return r.Need(k); }

        // ---------- Verknüpfungen ----------

        public static void SplitIcon(string loc, out string file, out int idx)
        {
            int p = loc.LastIndexOf(',');
            long n;
            if (p > 0 && Regex.IsMatch(loc.Substring(p + 1).Trim(), @"^-?\d+$") &&
                long.TryParse(loc.Substring(p + 1).Trim(), out n))
            {
                file = loc.Substring(0, p); idx = (int)n; return;
            }
            file = loc; idx = 0;
        }

        static J LnkRead(string path)
        {
            var l = new ShellLink(path);
            try { return l.Read(); }
            finally { l.Close(); }
        }

        static LinkMap AllLinks()
        {
            string au = Common.AllUsersDesktop, me = Common.MyDesktop;
            var map = new LinkMap();
            var dirs = new List<string>();
            var names = new List<string>();
            foreach (var e in Directory.GetFileSystemEntries(au)) names.Add(System.IO.Path.GetFileName(e));
            foreach (var n in Py.SortBy(names, x => x))
            {
                string p = Join(au, n);
                if (Directory.Exists(p)) dirs.Add(p);
            }
            dirs.Add(au);
            dirs.Add(me);
            foreach (var d in dirs)
            {
                var files = new List<string>();
                foreach (var e in Directory.GetFileSystemEntries(d)) files.Add(System.IO.Path.GetFileName(e));
                foreach (var f in Py.SortBy(files, x => x))
                    if (Low(f).EndsWith(".lnk")) map.Set(f.Substring(0, f.Length - 4), Join(d, f));
            }
            return map;
        }

        static string FindLink(string name)
        {
            if (Low(name).EndsWith(".lnk") && (name.Contains("\\") || name.Contains("/")))
            {
                if (!Exists(name)) throw new OpError("keine Datei: " + name);
                return name;
            }
            var links = AllLinks();
            foreach (var k in links.Keys)
                if (Low(k) == Low(name)) return links[k];
            var hits = new List<string>();
            foreach (var k in links.Keys)
                if (Low(k).StartsWith(Low(name), StringComparison.Ordinal)) hits.Add(links[k]);
            if (hits.Count != 1)
            {
                string tail = "";
                if (hits.Count > 0)
                {
                    var b = new List<string>();
                    for (int i = 0; i < hits.Count && i < 8; i++)
                    {
                        string bn = BaseName(hits[i]);
                        b.Add(bn.Substring(0, Math.Max(0, bn.Length - 4)));
                    }
                    tail = ": " + string.Join(", ", b.ToArray());
                }
                throw new OpError(string.Format("'{0}' passt auf {1} Verknüpfungen{2}", name, hits.Count, tail));
            }
            return hits[0];
        }

        static string IconOf(J info)
        {
            string icon = (string)info["icon"];
            if (!icon.StartsWith(",")) return icon;
            string ln = Common.LauncherName();
            string args = (string)info["args"];
            if (ln != "" && args.ToLowerInvariant().Contains(ln))
            {
                var q = args.Split('"');
                if (q.Length >= 8) return q[5] + "\\" + q[7] + ",0";
            }
            return (string)info["target"] + ",0";
        }

        static void ShellRefresh(string path)
        {
            if (!string.IsNullOrEmpty(path))
            {
                IntPtr p = Marshal.StringToHGlobalUni(path);
                try { SHChangeNotify(0x00001000, 0x0005, p, IntPtr.Zero); }   // SHCNE_UPDATEDIR, PATHW
                finally { Marshal.FreeHGlobal(p); }
            }
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);   // SHCNE_ASSOCCHANGED
        }

        static void DesktopF5()
        {
            IntPtr view = FindWindowExW(FindWindowW("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
            IntPtr lv = view != IntPtr.Zero ? FindWindowExW(view, IntPtr.Zero, "SysListView32", null) : IntPtr.Zero;
            if (lv != IntPtr.Zero)
            {
                PostMessageW(lv, 0x100, (IntPtr)0x74, IntPtr.Zero);
                PostMessageW(lv, 0x101, (IntPtr)0x74, IntPtr.Zero);
            }
        }

        static string LinkDest(string name, string folder)
        {
            string au = Common.AllUsersDesktop;
            string d = string.IsNullOrEmpty(folder) || folder == "-" ? au : Join(au, folder);
            if (!Directory.Exists(d)) Directory.CreateDirectory(d);
            return Join(d, name + ".lnk");
        }

        static void BackupLink(string path)
        {
            string bdir = Cfg.Str("launcher_backup_dir");
            if (Py.Truthy(bdir) && Exists(path))
            {
                if (!Directory.Exists(bdir)) Directory.CreateDirectory(bdir);
                string b = BaseName(path);
                string dst = Join(bdir, string.Format("{0}.{1}.lnk", b.Substring(0, b.Length - 4),
                    DateTime.Now.ToString("yyyyMMdd-HHmmss")));
                File.WriteAllBytes(dst, File.ReadAllBytes(path));
            }
        }

        [Op("lnk")]
        static J Lnk(Conn c, Req r)
        {
            string act = r.Need("action");
            if (act == "show")
            {
                var outL = new List<object>();
                Key(r, "names");
                foreach (var n in r.Strs("names"))
                {
                    var info = LnkRead(FindLink(n));
                    info["icon_used"] = IconOf(info);
                    outL.Add(info);
                }
                return J.Ok("links", outL);
            }
            string name = r.Need("name");
            string path;
            if (act == "make" || act == "launcher")
            {
                try { path = FindLink(name); }
                catch (OpError) { path = LinkDest(name, r.Str("folder")); }
                string target, args, wdir, icon;
                if (act == "launcher")
                {
                    string script = Cfg.Need("launcher_script", "Startskript für lnk launcher");
                    BackupLink(path);
                    wdir = r.Need("dir").TrimEnd('\\');
                    string exe = r.Need("exe");
                    target = Join(Join(Common.SysRoot(), "system32"), "wscript.exe");
                    args = string.Format("\"{0}\" \"{1}\" \"{2}\" \"{3}\"", script, r.Need("image"), wdir, exe);
                    icon = r.Or("icon", wdir + "\\" + exe + ",0");
                }
                else
                {
                    target = r.Need("target");
                    args = r.Or("args", "");
                    wdir = r.Or("dir", null);
                    if (wdir == null) wdir = DirName(target);
                    icon = r.Str("icon", null);
                }
                var l = new ShellLink(path);
                try
                {
                    l.Set(target, args, wdir, icon, r.Bool("show") ? r.Int("show") : 1);
                    l.Save();
                }
                finally { l.Close(); }
            }
            else if (act == "icon")
            {
                path = FindLink(name);
                var l = new ShellLink(path);
                try { l.Set(null, null, null, r.Need("icon"), null); l.Save(); }
                finally { l.Close(); }
            }
            else if (act == "style")
            {
                path = FindLink(name);
                var l = new ShellLink(path);
                try { l.Set(null, null, null, null, r.Int("show")); l.Save(); }
                finally { l.Close(); }
            }
            else return J.Err("unbekannte lnk-Aktion: " + act);
            ShellRefresh(DirName(path));
            var res = LnkRead(path);
            res["icon_used"] = IconOf(res);
            return J.Ok("links", new List<object> { res });
        }

        // ---------- Desktop-Gruppen ----------

        static string SetFolderIcon(string folder, string loc)
        {
            if (loc.StartsWith("@")) loc = IconOf(LnkRead(FindLink(loc.Substring(1))));
            string f; int idx;
            SplitIcon(loc, out f, out idx);
            string warn = Exists(f) ? null : "Icon-Datei fehlt: " + f;
            string ini = Join(folder, "desktop.ini");
            if (Exists(ini))
            {
                SetFileAttributesW(ini, 0x80);
                File.Delete(ini);
            }
            string text = "[.ShellClassInfo]\r\nIconFile=" + f + "\r\nIconIndex=" + idx + "\r\nConfirmFileOp=0\r\n";
            if (!Directory.Exists(folder))
                throw new OpError("FileNotFoundError", "[Errno 2] No such file or directory: " + PyRepr(ini));
            File.WriteAllBytes(ini, Encoding.Default.GetBytes(text));
            SetFileAttributesW(ini, 0x2 | 0x4);
            uint a = GetFileAttributesW(folder);
            SetFileAttributesW(folder, (a & ~0x2u) | 0x1u);
            ShellRefresh(folder);
            return warn;
        }

        static bool MoveLink(string path, string folder)
        {
            string b = BaseName(path);
            string dest = LinkDest(b.Substring(0, Math.Max(0, b.Length - 4)), folder);
            if (Low(dest) == Low(path)) return false;
            if (Exists(dest)) File.Delete(dest);
            File.Move(path, dest);
            return true;
        }

        [Op("desk")]
        static J Desk(Conn c, Req r)
        {
            string act = r.Need("action");
            string au = Common.AllUsersDesktop;
            var msgs = new List<object>();
            if (act == "list")
            {
                var rows = new List<object>();
                string flt = Low(r.Or("filter", ""));
                var links = AllLinks();
                foreach (var k in Py.SortBy(links.Keys, x => Low(x)))
                {
                    string p = links[k];
                    if (flt != "" && !Low(k).Contains(flt)) continue;
                    var info = LnkRead(p);
                    string d = DirName(p);
                    string where = Low(d) == Low(au) ? "(Desktop)" : BaseName(d);
                    string ic = IconOf(info);
                    string f; int i;
                    SplitIcon(ic, out f, out i);
                    rows.Add(J.O("folder", where, "name", k, "icon", ic, "icon_ok", Exists(f)));
                }
                return J.Ok("links", rows);
            }
            if (act == "move")
            {
                string p = FindLink(r.Need("name"));
                MoveLink(p, r.Need("folder"));
                msgs.Add("ok");
            }
            else if (act == "foldericon")
            {
                string w = SetFolderIcon(Join(au, r.Need("folder")), r.Need("icon"));
                msgs.Add(w ?? "ok");
            }
            else if (act == "ungroup")
            {
                var links = AllLinks();
                int n = 0;
                foreach (var k in links.Keys)
                {
                    string p = links[k];
                    if (Low(DirName(p)) != Low(au) && MoveLink(p, "-")) n++;
                }
                msgs.Add(n + " zurück auf den Desktop");
            }
            else if (act == "group")
            {
                var patKeys = new List<string>();
                var pats = new Dictionary<string, string>();
                var icoKeys = new List<string>();
                var icons = new Dictionary<string, string>();
                string grp = null;
                foreach (var raw in Regex.Split(r.Need("map"), "\r\n|[\n\r\v\f\x1c\x1d\x1e\x85\u2028\u2029]"))
                {
                    string line = raw.Trim();
                    if (line == "" || line.StartsWith("#")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]")) grp = line.Substring(1, line.Length - 2);
                    else if (Low(line).StartsWith("icon="))
                    {
                        if (grp == null) throw new OpError("TypeError", "object of type 'NoneType' has no len()");
                        if (!icons.ContainsKey(grp)) icoKeys.Add(grp);
                        icons[grp] = line.Substring(5);
                    }
                    else if (grp != null)
                    {
                        string lk = Low(line);
                        if (!pats.ContainsKey(lk)) patKeys.Add(lk);
                        pats[lk] = grp;
                    }
                }
                int moved = 0;
                var links = AllLinks();
                foreach (var k in Py.SortBy(links.Keys, x => x))
                {
                    string p = links[k];
                    string best = null;
                    int bl = 0;
                    foreach (var pat in patKeys)
                        if (pat.Length > bl && Low(k).StartsWith(pat, StringComparison.Ordinal)) { best = pats[pat]; bl = pat.Length; }
                    if (best == null) msgs.Add("ohne Gruppe: " + k);
                    else if (MoveLink(p, best))
                    {
                        moved++;
                        msgs.Add(k + " -> " + best);
                    }
                }
                foreach (var g in icoKeys)
                {
                    string w = SetFolderIcon(Join(au, g), icons[g]);
                    if (w != null) msgs.Add(g + ": " + w);
                }
                msgs.Add(moved + " verschoben");
                DesktopF5();
            }
            else return J.Err("unbekannte desk-Aktion: " + act);
            ShellRefresh(au);
            return J.Ok("messages", msgs);
        }

        // ---------- Desktop-Icon-Positionen (ListView im Explorer-Prozess) ----------

        sealed class DeskItem { public int I; public string Name; public int X, Y; }

        static int FloorDiv(int a, int b) { return (int)Math.Floor((double)a / b); }

        [Op("deskpos")]
        static J DeskPos(Conn c, Req r)
        {
            IntPtr h = FindWindowA("Progman", null);
            h = FindWindowExA(h, IntPtr.Zero, "SHELLDLL_DefView", null);
            IntPtr lv = FindWindowExA(h, IntPtr.Zero, "SysListView32", null);
            if (lv == IntPtr.Zero) return J.Err("Desktop-ListView nicht gefunden");
            uint pid;
            GetWindowThreadProcessId(lv, out pid);
            IntPtr proc = OpenProcess(0x0008 | 0x0010 | 0x0020 | 0x0400, false, pid);
            IntPtr remote = VirtualAllocEx(proc, IntPtr.Zero, 4096, 0x1000, 0x04);
            try
            {
                long rem = remote.ToInt64();
                Func<int, int, byte[]> rread = (n, off) =>
                {
                    var buf = new byte[n];
                    IntPtr got;
                    if (n > 0) ReadProcessMemory(proc, new IntPtr(rem + off), buf, n, out got);
                    return buf;
                };
                Func<List<DeskItem>> items = () =>
                {
                    var res = new List<DeskItem>();
                    long count = SendMessageA(lv, 0x1004, IntPtr.Zero, IntPtr.Zero).ToInt64();
                    for (int i = 0; i < count; i++)
                    {
                        var item = new byte[52];
                        BitConverter.GetBytes((uint)0).CopyTo(item, 0);
                        BitConverter.GetBytes((uint)i).CopyTo(item, 4);
                        BitConverter.GetBytes((uint)(rem + 512)).CopyTo(item, 20);
                        BitConverter.GetBytes((uint)260).CopyTo(item, 24);
                        IntPtr wr;
                        WriteProcessMemory(proc, remote, item, 52, out wr);
                        int n = (int)SendMessageA(lv, 0x102D, (IntPtr)i, remote).ToInt64();
                        string name = Encoding.Default.GetString(rread(Math.Max(n, 0), 512));
                        SendMessageA(lv, 0x1010, (IntPtr)i, new IntPtr(rem + 2048));
                        int x = BitConverter.ToInt32(rread(4, 2048), 0);
                        int y = BitConverter.ToInt32(rread(4, 2052), 0);
                        res.Add(new DeskItem { I = i, Name = name, X = x, Y = y });
                    }
                    return res;
                };
                Func<string, List<DeskItem>, DeskItem> find = (name, its) =>
                {
                    var hits = its.FindAll(t => Low(t.Name) == Low(name));
                    if (hits.Count == 0) hits = its.FindAll(t => Low(t.Name).StartsWith(Low(name), StringComparison.Ordinal));
                    if (hits.Count != 1)
                    {
                        var ns = new List<string>();
                        foreach (var t in hits) ns.Add(t.Name);
                        throw new OpError(string.Format("'{0}' passt auf {1} Icons: {2}", name, hits.Count, string.Join(", ", ns.ToArray())));
                    }
                    return hits[0];
                };

                int s = (int)SendMessageA(lv, 0x1033, IntPtr.Zero, IntPtr.Zero).ToInt64();
                int cx = s & 0xFFFF, cy = (s >> 16) & 0xFFFF;
                var rc = new int[4];
                SystemParametersInfoA(0x30, 0, rc, 0);
                int ww = rc[2] - rc[0], wh = rc[3] - rc[1];

                Func<int, int, int[]> cell = (col, row) =>
                {
                    if (col < 0) col += FloorDiv(ww, cx);
                    if (row < 0) row += FloorDiv(wh, cy);
                    return new[] { col * cx + FloorDiv(cx - 32, 2), row * cy + 2 };
                };
                Action<int, int, int> setpos = (i, x, y) =>
                    SendMessageA(lv, 0x100F, (IntPtr)i, (IntPtr)((y << 16) | (x & 0xFFFF)));

                string warn = null;
                if ((GetWindowLongA(lv, -16) & 0x100) != 0)
                    warn = "'Automatisch anordnen' ist an, Positionen werden überschrieben";
                var its0 = items();
                string act = r.Str("action", "list");
                if (act == "move")
                {
                    var t = find(r.Need("name"), its0);
                    setpos(t.I, (int)r.Int("x"), (int)r.Int("y"));
                }
                else if (act == "grid")
                {
                    var t = find(r.Need("name"), its0);
                    var xy = cell((int)NeedInt(r, "col"), (int)NeedInt(r, "row"));
                    setpos(t.I, xy[0], xy[1]);
                }
                else if (act == "row")
                {
                    var sel = new List<DeskItem>();
                    if (r.Bool("rest"))
                    {
                        foreach (var t in its0) if (Array.IndexOf(DeskSystem, Low(t.Name)) < 0) sel.Add(t);
                        sel = Py.SortBy(sel, t => Low(t.Name));
                    }
                    else
                    {
                        Key(r, "names");
                        foreach (var n in r.Strs("names")) sel.Add(find(n, its0));
                    }
                    for (int col = 0; col < sel.Count; col++)
                    {
                        var xy = cell(col, (int)NeedInt(r, "row"));
                        setpos(sel[col].I, xy[0], xy[1]);
                    }
                }
                else if (act != "list") return J.Err("unbekannte deskpos-Aktion: " + act);
                var rows = new List<object>();
                foreach (var t in items())
                    rows.Add(J.O("name", t.Name, "x", (long)t.X, "y", (long)t.Y,
                        "col", (long)Math.Round(t.X / (double)cx), "row", (long)Math.Round(t.Y / (double)cy)));
                return J.Ok("icons", rows, "grid", new List<object> { (long)cx, (long)cy },
                    "work", new List<object> { (long)ww, (long)wh }, "warning", warn);
            }
            finally
            {
                VirtualFreeEx(proc, remote, 0, 0x8000);
                CloseHandle(proc);
            }
        }

        static long NeedInt(Req r, string k) { Key(r, k); return r.Int(k); }

        // ---------- Datei-/Spiele-Versionen ----------

        static string PyRepr(string s) { return "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'"; }

        static J ExeVersion(string path)
        {
            var o = J.O("path", path);
            bool isDir = Directory.Exists(path);
            if (!isDir && !File.Exists(path)) { o["missing"] = true; return o; }
            long size = isDir ? 0 : new FileInfo(path).Length;
            o["size"] = size;
            o["date"] = (isDir ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path)).ToString("yyyy-MM-dd");
            if (size <= 64L * 1024 * 1024)
            {
                if (isDir) throw new OpError("PermissionError", "[Errno 13] Permission denied: " + PyRepr(path));
                try
                {
                    using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var md5 = MD5.Create())
                        o["md5"] = Server.Hex(md5.ComputeHash(f));
                }
                catch (UnauthorizedAccessException) { throw new OpError("PermissionError", "[Errno 13] Permission denied: " + PyRepr(path)); }
                catch (IOException) { throw new OpError("PermissionError", "[Errno 13] Permission denied: " + PyRepr(path)); }
            }
            uint n = GetFileVersionInfoSizeW(path, IntPtr.Zero);
            if (n == 0) return o;
            IntPtr buf = Marshal.AllocHGlobal((int)n);
            try
            {
                if (!GetFileVersionInfoW(path, 0, n, buf)) return o;
                IntPtr ptr; uint ln;
                if (VerQueryValueW(buf, "\\", out ptr, out ln) && ln >= 52)
                {
                    var d = new uint[13];
                    for (int i = 0; i < 13; i++) d[i] = unchecked((uint)Marshal.ReadInt32(ptr, i * 4));
                    if (d[0] == 0xFEEF04BD)
                    {
                        o["file_version"] = string.Format("{0}.{1}.{2}.{3}", d[2] >> 16, d[2] & 0xFFFF, d[3] >> 16, d[3] & 0xFFFF);
                        o["product_version"] = string.Format("{0}.{1}.{2}.{3}", d[4] >> 16, d[4] & 0xFFFF, d[5] >> 16, d[5] & 0xFFFF);
                    }
                }
                var langs = new List<string>();
                if (VerQueryValueW(buf, "\\VarFileInfo\\Translation", out ptr, out ln) && ln >= 4)
                {
                    int cnt = (int)(ln / 2);
                    var arr = new ushort[cnt];
                    for (int i = 0; i < cnt; i++) arr[i] = unchecked((ushort)Marshal.ReadInt16(ptr, i * 2));
                    for (int i = 0; i < cnt - 1; i += 2) langs.Add(arr[i].ToString("x4") + arr[i + 1].ToString("x4"));
                }
                var strings = new J();
                var all = new List<string>(langs);
                all.AddRange(new[] { "040904b0", "040904e4", "040704b0", "040704e4" });
                string[] keys = { "ProductName", "ProductVersion", "FileVersion", "FileDescription", "CompanyName",
                    "LegalCopyright", "OriginalFilename", "SpecialBuild", "PrivateBuild", "Comments" };
                foreach (var lang in all)
                    foreach (var key in keys)
                    {
                        if (strings.ContainsKey(key)) continue;
                        if (VerQueryValueW(buf, "\\StringFileInfo\\" + lang + "\\" + key, out ptr, out ln) && ln != 0)
                        {
                            string val = Marshal.PtrToStringUni(ptr, (int)ln).TrimEnd('\0').Trim();
                            if (val != "") strings[key] = val;
                        }
                    }
                o["strings"] = strings;
                var ll = new List<object>();
                foreach (var l in langs) ll.Add(l);
                o["langs"] = ll;
                return o;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static readonly Regex GameMarkers = new Regex(
            @"^(goggame-\d+\.info|gameinfo|version(\.txt|\.ini|\.dat)?|.*\.ver|" +
            @"readme.*|liesmich.*|patch.*\.txt|changes.*|changelog.*|.*update.*\.txt|whatsnew.*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        static string GameRoot(string exe)
        {
            string gdir = (Cfg.Str("games_dir") ?? "").TrimEnd('\\');
            string norm = NormPath(exe);
            if (gdir != "" && Low(norm).StartsWith(Low(gdir) + "\\", StringComparison.Ordinal))
            {
                string first = norm.Substring(gdir.Length + 1).Split('\\')[0];
                if (first != "" && Low(first) != Low(BaseName(norm)))
                    return gdir + "\\" + first;
            }
            return DirName(exe);
        }

        static bool Walk(string root, string bas, J found)
        {
            // gibt true zurück, wenn der Lauf abgebrochen werden soll (mehr als 40 Funde)
            var files = new List<string>();
            var dirs = new List<string>();
            try
            {
                foreach (var f in Directory.GetFiles(bas)) files.Add(System.IO.Path.GetFileName(f));
                foreach (var d in Directory.GetDirectories(bas)) dirs.Add(System.IO.Path.GetFileName(d));
            }
            catch (Exception) { return false; }
            bool prune = CountBs(bas.Substring(root.Length)) >= 2;
            foreach (var f in files)
            {
                if (!GameMarkers.IsMatch(f)) continue;
                string p = Join(bas, f);
                string rel = p.Substring(root.Length + 1);
                string low = Low(f);
                if (low.StartsWith("goggame-") || low == "gameinfo" || low.StartsWith("version"))
                {
                    try
                    {
                        byte[] data;
                        using (var fh = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            var b = new byte[4096];
                            int got = 0, k;
                            while (got < b.Length && (k = fh.Read(b, got, b.Length - got)) > 0) got += k;
                            data = new byte[got];
                            Array.Copy(b, data, got);
                        }
                        string txt = Encoding.GetEncoding(28591).GetString(data);
                        object val = null;
                        bool isDict = false;
                        if (low.StartsWith("goggame-"))
                        {
                            try
                            {
                                var j = Json.Parse(txt) as J;
                                if (j != null)
                                {
                                    var dd = new J();
                                    foreach (var key in new[] { "name", "gameId", "buildId", "version", "language", "languages" })
                                    {
                                        object v;
                                        if (j.TryGetValue(key, out v) && Py.Truthy(v)) dd[key] = v;
                                    }
                                    val = dd; isDict = true;
                                }
                            }
                            catch (Exception) { }
                        }
                        if (!isDict)
                        {
                            string t = txt.Trim();
                            val = t.Length > 400 ? t.Substring(0, 400) : t;
                        }
                        found[rel] = val;
                    }
                    catch (IOException) { found[rel] = null; }
                    catch (UnauthorizedAccessException) { found[rel] = null; }
                }
                else found[rel] = null;
            }
            if (found.Count > 40) return true;
            if (prune) return false;
            foreach (var d in dirs)
                if (Walk(root, Join(bas, d), found)) return true;
            return false;
        }

        static int CountBs(string s) { int n = 0; foreach (char ch in s) if (ch == '\\') n++; return n; }

        static J GameMarkersOf(string root)
        {
            var found = new J();
            Walk(root, root, found);
            return found;
        }

        static J LinkGame(string name, string path)
        {
            var info = LnkRead(path);
            string target = (string)info["target"];
            string args = (string)info["args"] ?? "";
            string img = null, exe;
            string ln = Common.LauncherName();
            if (Low(target).EndsWith("wscript.exe") && ln != "" && Low(args).Contains(ln))
            {
                var q = new List<string>();
                foreach (Match m in Regex.Matches(args, "\"([^\"]*)\"")) q.Add(m.Groups[1].Value);
                if (q.Count >= 4) { img = q[1]; exe = Join(q[2], q[3]); }
                else exe = target;
            }
            else exe = target;
            return J.O("name", name, "folder", BaseName(DirName(path)), "exe", exe,
                "args", Py.Truthy(img) ? null : args, "image", img, "root", GameRoot(exe));
        }

        [Op("games")]
        static J Games(Conn c, Req r)
        {
            string filt = Low(r.Or("filter", ""));
            var progs = Common.CollectPrograms();
            var outL = new List<object>();
            var links = AllLinks();
            foreach (var name in Py.SortBy(links.Keys, x => Low(x)))
            {
                string path = links[name];
                if (filt != "" && !Low(name).Contains(filt)) continue;
                J g;
                try { g = LinkGame(name, path); }
                catch (Exception e)
                {
                    if (e is EndOfConnection) throw;
                    outL.Add(J.O("name", name, "error", Server.ErrText(e)));
                    continue;
                }
                string exe = (string)g["exe"];
                if (!Low(exe).EndsWith(".exe") || Low(exe).Contains("\\windows\\"))
                {
                    g["skip"] = true;
                    outL.Add(g);
                    continue;
                }
                g["exe_info"] = ExeVersion(exe);
                string root = (string)g["root"];
                g["markers"] = Directory.Exists(root) && root.TrimEnd('\\').Length > 3 ? (object)GameMarkersOf(root) : new J();
                var un = new List<object>();
                foreach (var p in progs)
                {
                    string loc = (string)p["location"];
                    if (!Py.Truthy(loc)) continue;
                    if ((NormCase(loc).TrimEnd('\\') + "\\").StartsWith(NormCase(root) + "\\", StringComparison.Ordinal))
                        un.Add(J.O("name", p["name"], "version", p["version"], "date", p["date"]));
                }
                g["uninstall"] = un;
                outL.Add(g);
            }
            return J.Ok("games", outL);
        }

        static string NormCase(string s) { return Low(s.Replace('/', '\\')); }

        [Op("ver")]
        static J Ver(Conn c, Req r)
        {
            var outJ = new J();
            Key(r, "paths");
            foreach (var p in r.Strs("paths"))
            {
                try { outJ[p] = ExeVersion(p); }
                catch (Exception e) { outJ[p] = J.O("error", e.Message); }
            }
            return J.Ok("files", outJ);
        }
    }
}
