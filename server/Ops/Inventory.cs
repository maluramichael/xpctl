using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace XpServer
{
    /// <summary>Inventar (nur lesend): updates, programs, sysinfo, reg, settings, productkey.</summary>
    public static class Inventory
    {
        // ---------- P/Invoke ----------

        [DllImport("kernel32.dll")] static extern int GetSystemDefaultLCID();
        [DllImport("kernel32.dll")] static extern ushort GetUserDefaultUILanguage();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int GetLocaleInfoW(int lcid, int type, StringBuilder buf, int len);
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(byte[] buf);
        [DllImport("kernel32.dll")] static extern uint GetLogicalDrives();
        [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetDiskFreeSpaceExW(string root, out ulong avail, out ulong total, out ulong free);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int FormatMessageW(int flags, IntPtr src, int msgId, int langId, StringBuilder buf, int size, IntPtr args);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettingsW(string dev, uint mode, byte[] dm);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegOpenKeyExW(UIntPtr hKey, string sub, int opts, int sam, out UIntPtr res);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegQueryValueExW(UIntPtr hKey, string name, IntPtr res, out int type, byte[] data, ref int dataLen);

        // ---------- Hilfen ----------

        static object G(J v, string k, object def)
        {
            object o;
            return v != null && v.TryGetValue(k, out o) ? o : def;
        }

        static string Str(object o) { return Py.Repr(o); }

        /// <summary>a or b: erster wahrer Wert.</summary>
        static object Or(object a, object b) { return Py.Truthy(a) ? a : b; }

        /// <summary>str.splitlines() wie Python (inkl. der Unicode-Trenner).</summary>
        static List<string> SplitLines(string s)
        {
            var r = new List<string>();
            var cur = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == (char)13 || c == (char)10 || c == (char)11 || c == (char)12 || (c >= (char)0x1c && c <= (char)0x1e)
                    || c == (char)0x85 || c == (char)0x2028 || c == (char)0x2029)
                {
                    if (c == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;
                    r.Add(cur.ToString());
                    cur.Length = 0;
                }
                else cur.Append(c);
            }
            if (cur.Length > 0) r.Add(cur.ToString());
            return r;
        }

        /// <summary>Wie winreg.OpenKey: Schlüssel oder Fehlercode.</summary>
        static Reg.Key OpenRc(UIntPtr hive, string path, out int rc)
        {
            UIntPtr h;
            rc = RegOpenKeyExW(hive, path ?? "", 0, 0x20019, out h);
            return rc == 0 ? new Reg.Key { H = h } : null;
        }

        /// <summary>Text eines OSError mit WinError wie Python 3.4: "[WinError n] Meldung".</summary>
        static string WinErr(int rc)
        {
            var sb = new StringBuilder(1024);
            int n = FormatMessageW(0x1000 | 0x200, IntPtr.Zero, rc, 0x0400, sb, sb.Capacity, IntPtr.Zero);
            string m = n > 0 ? sb.ToString(0, n) : "Unknown error";
            int e = m.Length;
            while (e > 0 && (m[e - 1] <= ' ' || m[e - 1] == '.')) e--;
            return "[WinError " + rc + "] " + m.Substring(0, e);
        }

        static List<string> Sorted(IEnumerable<string> l)
        {
            return l.OrderBy(x => x, StringComparer.Ordinal).ToList();
        }

        static List<object> Objs(IEnumerable<string> l)
        {
            return l.Cast<object>().ToList();
        }

        // ---------- updates / programs ----------

        static string KbOf(params string[] texts)
        {
            foreach (var t in texts)
            {
                var m = Common.KbRe.Match(t ?? "");
                if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
            }
            return "";
        }

        static string CategoryOf(object release, string text)
        {
            string r = (release == null ? "" : Str(release)).Trim();
            if (r != "") return r;
            string t = (text ?? "").ToLowerInvariant();
            if (t.Contains("security update")) return "Security Update";
            if (t.Contains("hotfix")) return "Hotfix";
            if (t.Contains("service pack")) return "Service Pack";
            if (t.Contains("rollup")) return "Update Rollup";
            return "Update";
        }

        static string StrOr(object o)
        {
            return Py.Truthy(o) ? Str(o) : "";
        }

        class Upd
        {
            public string Kb = "", Title = "", Product = "", Category = "", Date = "";
            public List<object> Sources = new List<object>();
        }

        delegate void AddFn(string key, string kb, string title, string product, string category, string date, string source);

        static List<Upd> CollectUpdates()
        {
            var found = new Dictionary<string, Upd>();
            var order = new List<Upd>();
            AddFn add = (key, kb, title, product, category, date, source) =>
            {
                Upd e;
                if (!found.TryGetValue(key, out e)) { e = new Upd { Kb = kb }; found[key] = e; order.Add(e); }
                if (title != "" && (e.Title == "" || title.Length > e.Title.Length)) e.Title = title;
                if (product != "" && e.Product == "") e.Product = product;
                if (category != "" && e.Category == "") e.Category = category;
                if (date != "" && e.Date == "") e.Date = date;
                if (!e.Sources.Contains(source)) e.Sources.Add(source);
            };

            const string bas = @"SOFTWARE\Microsoft\Updates";
            var root = Reg.Read(bas);
            foreach (var prod in (root != null ? root.Subs : new List<string>()))
            {
                var stack = new List<object[]>();
                stack.Add(new object[] { bas + "\\" + prod, prod, 1 });
                while (stack.Count > 0)
                {
                    var top = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    string path = (string)top[0], label = (string)top[1];
                    int depth = (int)top[2];
                    var node = Reg.Read(path);
                    if (node == null) continue;
                    foreach (var sub in node.Subs)
                    {
                        if (sub.ToLowerInvariant() == "filelist") continue;
                        string child = path + "\\" + sub;
                        string kb = KbOf(sub);
                        if (kb != "")
                        {
                            var c = Reg.Read(child, false);
                            if (c == null) continue;
                            var cv = c.Vals;
                            string title = StrOr(Or(G(cv, "Description", null), Or(G(cv, "PackageName", null), "")));
                            string date = Common.NormDate(G(cv, "InstalledDate", null));
                            if (date == "") date = c.Stamp;
                            add(kb, kb, title, label, CategoryOf(Or(G(cv, "ReleaseType", null), G(cv, "Type", null)), title), date, "Updates");
                        }
                        else if (depth < 3)
                            stack.Add(new object[] { child, depth == 1 ? label + " " + sub : label, depth + 1 });
                    }
                }
            }

            var u = Reg.Read(Common.HklmUninstall);
            foreach (var name in (u != null ? u.Subs : new List<string>()))
            {
                var e = Reg.Read(Common.HklmUninstall + "\\" + name, false);
                if (e == null) continue;
                var v = e.Vals;
                if (Common.ProgramKind(name, v) != "update") continue;
                string disp = StrOr(G(v, "DisplayName", null));
                string kb = KbOf(name, disp);
                string key = kb != "" ? kb : "name:" + (disp != "" ? disp : name);
                string date = Common.NormDate(G(v, "InstallDate", null));
                if (date == "") date = e.Stamp;
                add(key, kb, disp, StrOr(G(v, "ParentDisplayName", null)), CategoryOf(G(v, "ReleaseType", null), disp), date, "Uninstall");
            }
            return order.OrderBy(x => x.Date, StringComparer.Ordinal).ThenBy(x => x.Kb, StringComparer.Ordinal)
                .ThenBy(x => x.Title, StringComparer.Ordinal).ToList();
        }

        static J WinpatchState()
        {
            var nt = Reg.Read(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false);
            var v = nt != null ? nt.Vals : new J();
            var spCtl = Reg.Read(@"SYSTEM\CurrentControlSet\Control\Windows", false);
            object csd = spCtl != null ? G(spCtl.Vals, "CSDVersion", null) : null;
            var pos = Reg.Read(@"SYSTEM\WPA\PosReady", false);
            object posState = null;
            if (pos != null)
            {
                var d = new J();
                foreach (var kv in pos.Vals) d[kv.Key] = kv.Value;
                posState = d;
            }
            return J.O(
                "product", G(v, "ProductName", ""), "version", G(v, "CurrentVersion", ""),
                "build", G(v, "CurrentBuildNumber", ""), "csd", G(v, "CSDVersion", ""),
                "sp_level", csd is long ? (object)((long)csd >> 8) : null,
                "posready_key", pos != null, "posready", posState);
        }

        [Op("updates")]
        static J Updates(Conn c, Req r)
        {
            var ups = new List<object>();
            foreach (var x in CollectUpdates())
                ups.Add(J.O("kb", x.Kb, "title", x.Title, "product", x.Product, "category", x.Category,
                    "date", x.Date, "sources", x.Sources));
            var st = WinpatchState();
            return J.Ok("state", st, "updates", ups);
        }

        [Op("programs")]
        static J Programs(Conn c, Req r)
        {
            return J.Ok("programs", Common.CollectPrograms().Cast<object>().ToList());
        }

        // ---------- sysinfo ----------

        static readonly Dictionary<string, string> DxNames = new Dictionary<string, string> {
            {"4.02.0095", "1.0"}, {"4.03.00.1096", "2.0"}, {"4.04.0068", "3.0"}, {"4.04.0069", "3.0"},
            {"4.05.00.0155", "5.0"}, {"4.05.01.1998", "5.0"}, {"4.06.02.0436", "6.0"},
            {"4.07.00.0700", "7.0"}, {"4.07.00.0716", "7.0a"}, {"4.08.00.0400", "8.0"},
            {"4.08.01.0881", "8.1"}, {"4.08.01.0810", "8.1"}, {"4.09.0000.0900", "9.0"},
            {"4.09.0000.0901", "9.0a"}, {"4.09.0000.0902", "9.0b"}, {"4.09.0000.0904", "9.0c"},
            {"4.09.00.0900", "9.0"}, {"4.09.00.0901", "9.0a"}, {"4.09.00.0902", "9.0b"},
            {"4.09.00.0904", "9.0c"} };

        static string LocaleName(int lcid)
        {
            var sb = new StringBuilder(128);
            GetLocaleInfoW(lcid, 0x1001, sb, 128);
            return sb.ToString();
        }

        [Op("sysinfo")]
        static J SysInfo(Conn c, Req r)
        {
            var o = J.Ok();
            var nt = Reg.Read(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false);
            var nv = nt != null ? nt.Vals : new J();
            var st = WinpatchState();
            string installed = "";
            object idt = G(nv, "InstallDate", null);
            if (idt is long)
            {
                try
                {
                    installed = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds((long)idt).ToLocalTime().ToString("yyyy-MM-dd");
                }
                catch { installed = ""; }
            }
            o["windows"] = J.O("product", st["product"], "version", st["version"], "build", st["build"],
                "sp", st["csd"], "owner", G(nv, "RegisteredOwner", ""), "org", G(nv, "RegisteredOrganization", ""),
                "installed", installed, "posready", Py.Truthy(st["posready_key"]));
            string host = Environment.GetEnvironmentVariable("COMPUTERNAME");
            o["host"] = !string.IsNullOrEmpty(host) ? host : System.Net.Dns.GetHostName();
            try
            {
                int sysl = GetSystemDefaultLCID();
                int uil = GetUserDefaultUILanguage();
                string name = LocaleName(sysl);
                string ui = LocaleName(uil);
                o["language"] = J.O("system", name, "system_lcid", "0x" + (sysl & 0xFFFF).ToString("x4"),
                    "ui", ui, "ui_lcid", "0x" + uil.ToString("x4"));
            }
            catch (Exception e) { o["language"] = J.O("error", e.Message); }

            var cpu0 = Reg.Read(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", false);
            var cpus = Reg.Read(@"HARDWARE\DESCRIPTION\System\CentralProcessor");
            string cname = "";
            if (cpu0 != null)
                cname = string.Join(" ", Str(G(cpu0.Vals, "ProcessorNameString", "")).Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            o["cpu"] = J.O("name", cname, "mhz", cpu0 != null ? G(cpu0.Vals, "~MHz", null) : null,
                "ident", cpu0 != null ? G(cpu0.Vals, "Identifier", "") : "",
                "cores", cpus != null ? (object)(long)cpus.Subs.Count : null);

            var ms = new byte[64];
            BitConverter.GetBytes(64).CopyTo(ms, 0);
            if (GlobalMemoryStatusEx(ms))
            {
                o["ram"] = J.O("total_mb", (long)(BitConverter.ToUInt64(ms, 8) / (1024 * 1024)),
                    "free_mb", (long)(BitConverter.ToUInt64(ms, 16) / (1024 * 1024)),
                    "load_pct", (long)BitConverter.ToUInt32(ms, 4));
            }

            var drives = new List<object>();
            uint mask = GetLogicalDrives();
            for (int i = 0; i < 26; i++)
            {
                if ((mask & (1u << i)) == 0) continue;
                string root = (char)('A' + i) + ":\\";
                int t = Common.GetDriveTypeW(root);
                string kind;
                switch (t)
                {
                    case 2: kind = "Wechsel"; break;
                    case 3: kind = "Fest"; break;
                    case 4: kind = "Netz"; break;
                    case 5: kind = "CD/DVD"; break;
                    case 6: kind = "RAM"; break;
                    default: kind = t.ToString(); break;
                }
                var d = J.O("drive", root, "type", kind);
                if (t == 2 || t == 3 || t == 4)
                {
                    ulong avail, total, free;
                    uint old = SetErrorMode(1);
                    bool ok;
                    try { ok = GetDiskFreeSpaceExW(root, out avail, out total, out free); }
                    finally { SetErrorMode(old); }
                    if (ok)
                    {
                        d["total_mb"] = (long)(total / (1024 * 1024));
                        d["free_mb"] = (long)(free / (1024 * 1024));
                    }
                }
                drives.Add(d);
            }
            o["drives"] = drives;

            var ie = Reg.Read(@"SOFTWARE\Microsoft\Internet Explorer", false);
            var iev = ie != null ? ie.Vals : new J();
            o["ie"] = StrOr(Or(G(iev, "svcVersion", null), G(iev, "Version", null)));

            var dx = Reg.Read(@"SOFTWARE\Microsoft\DirectX", false);
            string dxr = dx != null ? Str(G(dx.Vals, "Version", "")) : "";
            string dxn;
            if (!DxNames.TryGetValue(dxr, out dxn)) dxn = "";
            o["directx"] = J.O("raw", dxr, "name", dxn);

            var net = new List<object>();
            const string ndpBase = @"SOFTWARE\Microsoft\NET Framework Setup\NDP";
            var ndp = Reg.Read(ndpBase);
            foreach (var sub in (ndp != null ? ndp.Subs : new List<string>()))
            {
                if (!sub.ToLowerInvariant().StartsWith("v")) continue;
                string bas = ndpBase + "\\" + sub;
                var cands = new List<string[]>();
                cands.Add(new[] { sub, bas });
                var node = Reg.Read(bas);
                foreach (var cc in (node != null ? node.Subs : new List<string>()))
                    if (cc == "Client" || cc == "Full") cands.Add(new[] { sub + " " + cc, bas + "\\" + cc });
                foreach (var cd in cands)
                {
                    var rr = Reg.Read(cd[1], false);
                    object inst = rr != null ? G(rr.Vals, "Install", null) : null;
                    if (inst is long && (long)inst == 1)
                        net.Add(J.O("name", cd[0], "version", Str(G(rr.Vals, "Version", "")), "sp", G(rr.Vals, "SP", null)));
                }
            }
            o["dotnet"] = net;
            return o;
        }

        // ---------- reg ----------

        /// <summary>Python 3.4 winreg liefert REG_SZ ohne Abschneiden an der ersten NUL (nur eine End-NUL entfällt).
        /// Reg.Convert (Core) kappt an der ersten NUL; hier wird der Rohwert neu gelesen.</summary>
        static object RawSz(Reg.Key k, string name, object fallback)
        {
            int type, len = 0;
            if (RegQueryValueExW(k.H, name, IntPtr.Zero, out type, null, ref len) != 0 && len == 0) return fallback;
            var data = new byte[len + 2];
            len = data.Length;
            if (RegQueryValueExW(k.H, name, IntPtr.Zero, out type, data, ref len) != 0) return fallback;
            int n = len / 2;
            if (n > 0 && data[(n - 1) * 2] == 0 && data[(n - 1) * 2 + 1] == 0) n--;
            return Encoding.Unicode.GetString(data, 0, n * 2);
        }

        static J RegTree(string hive, string path, int depth, bool withValues, long[] budget, bool top)
        {
            var node = J.O("path", path);
            int rc;
            var k = OpenRc(Reg.Hive(hive), path, out rc);
            if (k == null)
            {
                node["error"] = WinErr(rc);
                return node;
            }
            budget[0] -= 1;
            var vals = new List<object>();
            var subs = new List<string>();
            using (k)
            {
                int nsubs, nvals; long last;
                k.Info(out nsubs, out nvals, out last);
                if (top || withValues)
                {
                    for (int i = 0; i < nvals; i++)
                    {
                        var v = k.EnumValue(i);
                        if (v == null) break;
                        string tn;
                        if (!Reg.TypeNames.TryGetValue(v.Type, out tn)) tn = v.Type.ToString();
                        object data = v.Data;
                        if (v.Type == 1 || v.Type == 2) data = RawSz(k, v.Name, data);
                        vals.Add(J.O("name", v.Name, "type", tn, "data", Reg.Show(data)));
                    }
                }
                node["nvalues"] = (long)nvals;
                for (int i = 0; i < nsubs; i++)
                {
                    var s = k.EnumKey(i);
                    if (s == null) break;
                    subs.Add(s);
                }
            }
            node["values"] = vals;
            node["subkeys"] = Objs(subs);
            if (depth > 0)
            {
                var children = new List<object>();
                node["children"] = children;
                foreach (var s in subs)
                {
                    if (budget[0] <= 0) { node["truncated"] = true; break; }
                    children.Add(RegTree(hive, path != "" ? path + "\\" + s : s, depth - 1, withValues, budget, false));
                }
            }
            return node;
        }

        [Op("reg")]
        static J RegOp(Conn c, Req r)
        {
            string hive, path;
            Reg.Split(r.Need("key"), out hive, out path);
            Reg.Hive(hive);
            var budget = new long[] { r.Int("limit", 3000) };
            var node = RegTree(hive, path, (int)Math.Min(r.Int("recurse", 0), 8), r.Bool("values"), budget, true);
            if (node.ContainsKey("error"))
                return J.Err(hive + "\\" + path + ": " + node["error"]);
            return J.Ok("hive", hive.ToUpperInvariant(), "tree", node);
        }

        // ---------- settings ----------

        class Sec
        {
            public string Title;
            public List<object> Items = new List<object>();
            public Sec(string t) { Title = t; }

            public void Add(string k, object v, string reg = "", string note = "")
            {
                Items.Add(J.O("k", k, "v", v, "reg", reg, "note", note));
            }

            public object Get(string k, string hive, string path, string name, Dictionary<object, string> decode = null)
            {
                object v = Reg.Get(hive, path, name);
                string note = "";
                if (decode != null && v != null && !(v is List<object>))
                {
                    string n;
                    if (decode.TryGetValue(v, out n)) note = n;
                }
                Add(k, v, hive + "\\" + path + "\\" + (name != "" ? name : "(Standard)"), note);
                return v;
            }

            public J Last { get { return (J)Items[Items.Count - 1]; } }
        }

        /// <summary>Decode-Tabelle: Schlüssel int oder string (wie im Python-dict, beide nicht gleich).</summary>
        static Dictionary<object, string> D(params object[] kv)
        {
            var d = new Dictionary<object, string>();
            for (int i = 0; i + 1 < kv.Length; i += 2)
            {
                object k = kv[i];
                if (k is int) k = (long)(int)k;
                d[k] = (string)kv[i + 1];
            }
            return d;
        }

        const string HKLM_S = "HKLM", HKCU_S = "HKCU";
        const string EXPL = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
        const string FWP = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";
        const string SVCS = @"SYSTEM\CurrentControlSet\Services";
        const string CLASS_VIDEO = @"SYSTEM\CurrentControlSet\Control\Class\{4D36E968-E325-11CE-BFC1-08002BE10318}";
        const string CLASS_MEDIA = @"SYSTEM\CurrentControlSet\Control\Class\{4D36E96C-E325-11CE-BFC1-08002BE10318}";

        static string ShCmd(string cmd) { return Proc.Sh(cmd, 20); }

        static List<string> NetNames(string output, int columns = 0)
        {
            var names = new List<string>();
            bool on = false;
            foreach (var ln in SplitLines(output))
            {
                if (ln.StartsWith("-----", StringComparison.Ordinal)) { on = true; continue; }
                if (!on) continue;
                string low = ln.ToLowerInvariant();
                if (low.StartsWith("the command", StringComparison.Ordinal) || low.StartsWith("der befehl", StringComparison.Ordinal)) break;
                if (ln.Trim() == "") continue;
                if (columns > 0)
                {
                    for (int i = 0; i < ln.Length; i += columns)
                    {
                        string x = ln.Substring(i, Math.Min(columns, ln.Length - i)).Trim();
                        if (x != "") names.Add(x);
                    }
                }
                else names.Add(ln.Trim());
            }
            return names;
        }

        /// <summary>display_modes: Aktuelle und gespeicherte Auflösung, größter Modus, Anzahl.</summary>
        static SortedList<string, object> DisplayModes()
        {
            var res = new SortedList<string, object>(StringComparer.Ordinal);
            var dm = new byte[220];
            Action reset = () => { Array.Clear(dm, 0, dm.Length); BitConverter.GetBytes((ushort)220).CopyTo(dm, 68); };
            Func<string> fmt = () => string.Format("{0}x{1}, {2} Bit, {3} Hz", BitConverter.ToUInt32(dm, 172),
                BitConverter.ToUInt32(dm, 176), BitConverter.ToUInt32(dm, 168), BitConverter.ToUInt32(dm, 184));
            reset();
            if (EnumDisplaySettingsW(null, 0xFFFFFFFF, dm)) res["current"] = fmt();
            reset();
            if (EnumDisplaySettingsW(null, 0xFFFFFFFE, dm)) res["registry"] = fmt();
            uint bw = 0, bh = 0;
            bool any = false;
            long i = 0;
            while (i < 400)
            {
                reset();
                if (!EnumDisplaySettingsW(null, (uint)i, dm)) break;
                uint w = BitConverter.ToUInt32(dm, 172), h = BitConverter.ToUInt32(dm, 176);
                if (!any || w > bw || (w == bw && h > bh)) { bw = w; bh = h; any = true; }
                i++;
            }
            res["max_mode"] = any ? bw + "x" + bh : "";
            res["mode_count"] = i;
            return res;
        }

        static Sec SecExplorer()
        {
            var s = new Sec("Explorer und Taskleiste");
            string adv = EXPL + @"\Advanced";
            s.Get("Dateiendungen verbergen (HideFileExt)", HKCU_S, adv, "HideFileExt",
                D(0, "Endungen sichtbar", 1, "Endungen versteckt"));
            s.Get("Versteckte Dateien (Hidden)", HKCU_S, adv, "Hidden", D(1, "anzeigen", 2, "nicht anzeigen"));
            s.Get("Geschützte Systemdateien (ShowSuperHidden)", HKCU_S, adv, "ShowSuperHidden", D(0, "verbergen", 1, "anzeigen"));
            s.Get("Vollständiger Pfad in Titelleiste (FullPath)", HKCU_S,
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\CabinetState", "FullPath");
            s.Get("Einfache Dateifreigabe (forceguest)", HKLM_S, @"SYSTEM\CurrentControlSet\Control\Lsa",
                "forceguest", D(0, "aus (klassische Freigabe)", 1, "an (Gastzugriff)"));
            s.Get("Taskleiste fixiert (TaskbarSizeMove)", HKCU_S, adv, "TaskbarSizeMove", D(0, "fixiert", 1, "frei"));
            s.Get("Taskleisten-Gruppierung (TaskbarGlomLevel)", HKCU_S, adv, "TaskbarGlomLevel",
                D(0, "immer", 1, "bei Platzmangel", 2, "nie"));
            s.Get("Sprechblasen (EnableBalloonTips)", HKCU_S, adv, "EnableBalloonTips", D(0, "aus", 1, "an"));
            s.Get("Infofelder (ShowInfoTip)", HKCU_S, adv, "ShowInfoTip", D(0, "aus", 1, "an"));
            s.Get("Inaktive Symbole ausblenden (EnableAutoTray)", HKCU_S, EXPL, "EnableAutoTray", D(0, "nein", 1, "ja"));
            s.Get("Startmenü-Stil: StartMenuInit", HKCU_S, adv, "StartMenuInit");
            s.Get("Startmenü klassisch erzwungen (Policy NoSimpleStartMenu)", HKCU_S,
                @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoSimpleStartMenu",
                D(0, "XP-Stil", 1, "klassisch"));
            s.Get("Startmenü: Ausführen anzeigen (Start_ShowRun)", HKCU_S, adv, "Start_ShowRun");
            s.Get("Startmenü: Programme zählen (Start_TrackProgs)", HKCU_S, adv, "Start_TrackProgs");
            s.Get("Tour-Zähler (Tour RunCount)", HKCU_S,
                @"Software\Microsoft\Windows\CurrentVersion\Applets\Tour", "RunCount", D(0, "Tour ist abgeschaltet"));
            var icons = new Dictionary<string, string> {
                {"{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "Arbeitsplatz"},
                {"{450D8FBA-AD25-11D0-98A8-0800361B1103}", "Eigene Dateien"},
                {"{208D2C60-3AEA-1069-A2D7-08002B30309D}", "Netzwerkumgebung"},
                {"{871C5380-42A0-1069-A2EA-08002B30309D}", "Internet Explorer"} };
            foreach (var panel in new[] { "NewStartPanel", "ClassicStartMenu" })
                foreach (var guid in Sorted(icons.Keys))
                    s.Get("Desktopsymbol " + icons[guid] + " ausblenden (" + panel + ")", HKCU_S,
                        EXPL + @"\HideDesktopIcons" + "\\" + panel, guid, D(0, "sichtbar", 1, "versteckt"));
            return s;
        }

        static Sec SecDesign()
        {
            var s = new Sec("Desktop und Design");
            const string tm = @"Software\Microsoft\Windows\CurrentVersion\ThemeManager";
            s.Get("Design aktiv (ThemeActive)", HKCU_S, tm, "ThemeActive", D("0", "klassisch", "1", "XP-Design"));
            foreach (var n in new[] { "DllName", "ColorName", "SizeName" })
                s.Get("Design " + n, HKCU_S, tm, n);
            s.Get("Farbschema klassisch (Appearance)", HKCU_S, @"Control Panel\Appearance", "Current");
            const string desk = @"Control Panel\Desktop";
            s.Get("Hintergrundbild (Wallpaper)", HKCU_S, desk, "Wallpaper");
            s.Get("Hintergrund-Stil (WallpaperStyle)", HKCU_S, desk, "WallpaperStyle",
                D("0", "zentriert/Kachel", "2", "strecken", "6", "anpassen"));
            s.Get("Bildschirmschoner aktiv (ScreenSaveActive)", HKCU_S, desk, "ScreenSaveActive", D("0", "aus", "1", "an"));
            s.Get("Bildschirmschoner (SCRNSAVE.EXE)", HKCU_S, desk, "SCRNSAVE.EXE");
            s.Get("Bildschirmschoner Wartezeit s (ScreenSaveTimeout)", HKCU_S, desk, "ScreenSaveTimeout");
            s.Get("Schoner mit Kennwort (ScreenSaverIsSecure)", HKCU_S, desk, "ScreenSaverIsSecure");
            s.Get("Schriftglättung (FontSmoothing)", HKCU_S, desk, "FontSmoothing", D("0", "aus", "2", "an"));
            s.Get("ClearType (FontSmoothingType)", HKCU_S, desk, "FontSmoothingType", D(1, "Standard", 2, "ClearType"));
            s.Get("Fensterinhalt beim Ziehen (DragFullWindows)", HKCU_S, desk, "DragFullWindows", D("0", "aus", "1", "an"));
            s.Get("Menüverzögerung ms (MenuShowDelay)", HKCU_S, desk, "MenuShowDelay");
            s.Get("UserPreferencesMask", HKCU_S, desk, "UserPreferencesMask");
            s.Get("Fenster-Animation (MinAnimate)", HKCU_S, desk + @"\WindowMetrics", "MinAnimate", D("0", "aus", "1", "an"));
            try
            {
                foreach (var kv in DisplayModes())
                    s.Add("Bildschirm " + kv.Key, kv.Value, "EnumDisplaySettings");
            }
            catch (Exception e) { s.Add("Bildschirm", "Fehler: " + e.Message); }
            s.Get("Schrift-DPI (LogPixels)", HKLM_S,
                @"SYSTEM\CurrentControlSet\Hardware Profiles\Current\Software\Fonts", "LogPixels");
            s.Get("Soundschema (AppEvents Standard)", HKCU_S, @"AppEvents\Schemes", "");
            return s;
        }

        static bool IsDigits(string x)
        {
            if (x.Length == 0) return false;
            foreach (char ch in x) if (!char.IsDigit(ch)) return false;
            return true;
        }

        static Sec SecRegion()
        {
            var s = new Sec("Region, Tastatur, Zeit");
            const string intl = @"Control Panel\International";
            foreach (var n in new[] { "Locale", "sLanguage", "sCountry", "iCountry", "sShortDate", "sLongDate",
                "sTimeFormat", "sDecimal", "sThousand", "sList", "iMeasure", "iPaperSize", "s1159", "s2359" })
                s.Get("Format " + n, HKCU_S, intl, n);
            var pre = Reg.Vals(HKCU_S, @"Keyboard Layout\Preload");
            foreach (var n in pre.Keys.OrderBy(x => IsDigits(x) ? (x.Length > 15 ? long.MaxValue : long.Parse(x)) : 99).ToList())
                s.Add("Tastaturlayout Preload " + n, pre[n], @"HKCU\Keyboard Layout\Preload\" + n);
            var sub = Reg.Vals(HKCU_S, @"Keyboard Layout\Substitutes");
            foreach (var n in Sorted(sub.Keys))
                s.Add("Layout-Ersatz " + n, sub[n], @"HKCU\Keyboard Layout\Substitutes\" + n);
            var dpre = Reg.Vals("HKU", @".DEFAULT\Keyboard Layout\Preload");
            foreach (var n in Sorted(dpre.Keys))
                s.Add("Anmeldebildschirm Layout " + n, dpre[n], @"HKU\.DEFAULT\Keyboard Layout\Preload\" + n);
            s.Get("Eingabeumschalter (Hotkey)", HKCU_S, @"Keyboard Layout\Toggle", "Hotkey",
                D("1", "Alt+Shift", "2", "Strg+Shift", "3", "aus"));
            s.Get("System-Gebietsschema (Nls Default)", HKLM_S, @"SYSTEM\CurrentControlSet\Control\Nls\Language", "Default");
            s.Get("Installationssprache (InstallLanguage)", HKLM_S, @"SYSTEM\CurrentControlSet\Control\Nls\Language", "InstallLanguage");
            s.Get("ANSI-Codepage (ACP)", HKLM_S, @"SYSTEM\CurrentControlSet\Control\Nls\CodePage", "ACP");
            s.Get("OEM-Codepage (OEMCP)", HKLM_S, @"SYSTEM\CurrentControlSet\Control\Nls\CodePage", "OEMCP");
            const string tz = @"SYSTEM\CurrentControlSet\Control\TimeZoneInformation";
            object name = s.Get("Zeitzone (StandardName)", HKLM_S, tz, "StandardName");
            if (Py.Truthy(name))
            {
                string tzp = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Time Zones\" + Str(name);
                object disp = Reg.Get(HKLM_S, tzp, "Display");
                if (Py.Truthy(disp)) s.Add("Zeitzone Anzeigename", disp, @"HKLM\" + tzp + @"\Display");
            }
            object bias = s.Get("Zeitzone Bias min (Bias)", HKLM_S, tz, "Bias");
            if (bias is long && (long)bias > 0x7FFFFFFF)
            {
                s.Last["v"] = (long)bias - 0x100000000L;
                s.Last["note"] = "vorzeichenbehaftet, -60 = UTC+1";
            }
            s.Get("Sommerzeit automatisch aus? (DynamicDaylightTimeDisabled)", HKLM_S, tz,
                "DynamicDaylightTimeDisabled", D(0, "Umstellung an", 1, "Umstellung aus"));
            string w32 = SVCS + @"\W32Time\Parameters";
            s.Get("Zeitsynchronisation Typ (W32Time)", HKLM_S, w32, "Type");
            s.Get("Zeitserver (NtpServer)", HKLM_S, w32, "NtpServer");
            return s;
        }

        static string UserFlags(string name)
        {
            string output = ShCmd("net user \"" + name + "\"");
            string[] keep = { "Account active", "Password required", "Password expires", "Local Group Memberships",
                "Last logon", "User may change password" };
            var res = new List<string>();
            foreach (var ln in SplitLines(output))
                foreach (var k in keep)
                    if (ln.StartsWith(k, StringComparison.Ordinal))
                        res.Add(k + "=" + ln.Substring(k.Length).Trim());
            return string.Join("; ", res.ToArray());
        }

        static Sec SecUsers()
        {
            var s = new Sec("Benutzerkonten und Anmeldung");
            s.Add("Server läuft als Benutzer", Environment.GetEnvironmentVariable("USERNAME") ?? "", "%USERNAME%");
            var users = NetNames(ShCmd("net user"), 25);
            s.Add("Lokale Konten", string.Join(", ", users.ToArray()), "net user");
            foreach (var u in users)
                s.Add("Konto " + u, UserFlags(u), "net user " + u);
            foreach (var g in new[] { "Administrators", "Users", "Power Users" })
            {
                var m = NetNames(ShCmd("net localgroup \"" + g + "\""));
                s.Add("Gruppe " + g, string.Join(", ", m.ToArray()), "net localgroup");
            }
            const string wl = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
            s.Get("Auto-Login (AutoAdminLogon)", HKLM_S, wl, "AutoAdminLogon", D("0", "aus", "1", "an"));
            s.Get("Auto-Login Benutzer (DefaultUserName)", HKLM_S, wl, "DefaultUserName");
            s.Get("Auto-Login Domäne/PC (DefaultDomainName)", HKLM_S, wl, "DefaultDomainName");
            object pw = Reg.Get(HKLM_S, wl, "DefaultPassword");
            s.Add("Auto-Login Kennwort im Klartext hinterlegt", Py.Truthy(pw) ? "ja" : "nein",
                @"HKLM\" + wl + @"\DefaultPassword (Wert wird nie ausgegeben)");
            s.Get("Willkommensseite (LogonType)", HKLM_S, wl, "LogonType", D(0, "klassische Anmeldung", 1, "Willkommensseite"));
            s.Get("Schnelle Benutzerumschaltung (AllowMultipleTSSessions)", HKLM_S, wl,
                "AllowMultipleTSSessions", D(0, "aus", 1, "an"));
            s.Get("Shell (Winlogon)", HKLM_S, wl, "Shell");
            s.Get("Userinit (Winlogon)", HKLM_S, wl, "Userinit");
            s.Get("Registrierter Besitzer", HKLM_S, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "RegisteredOwner");
            s.Get("Registrierte Organisation", HKLM_S, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "RegisteredOrganization");
            const string pl = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
            foreach (var sub in Reg.Subs(HKLM_S, pl))
                if (sub.StartsWith("S-1-5-21", StringComparison.Ordinal))
                    s.Add("Profilordner", Reg.Get(HKLM_S, pl + "\\" + sub, "ProfileImagePath"), sub);
            return s;
        }

        static Sec SecQuiet()
        {
            var s = new Sec("Ruhe (Autoplay, Wiederherstellung, Meldungen, Energie)");
            const string pol = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
            s.Get("Autoplay Laufwerkstypen deaktiviert HKCU (NoDriveTypeAutoRun)", HKCU_S, pol, "NoDriveTypeAutoRun",
                D(255, "alle aus", 145, "0x91 (XP-Standard)", 177, "0xB1 (CD-ROM-Autoplay aus, sonst XP-Standard)"));
            s.Get("Autoplay HKLM (NoDriveTypeAutoRun)", HKLM_S, pol, "NoDriveTypeAutoRun", D(255, "alle aus", 145, "XP-Standard"));
            s.Get("CD-Autorun (Cdrom AutoRun)", HKLM_S, SVCS + @"\Cdrom", "AutoRun", D(0, "aus", 1, "an"));
            const string sr = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
            s.Get("Systemwiederherstellung abgeschaltet (DisableSR)", HKLM_S, sr, "DisableSR", D(0, "an", 1, "aus"));
            s.Get("Dienst sr Starttyp", HKLM_S, SVCS + @"\sr", "Start",
                D(0, "Boot", 1, "System", 2, "Auto", 3, "Manuell", 4, "Deaktiviert"));
            s.Get("Wiederherstellung Platten-% (DiskPercent)", HKLM_S, sr, "DiskPercent");
            const string eh = @"SOFTWARE\Microsoft\PCHealth\ErrorReporting";
            s.Get("Fehlerberichterstattung (DoReport)", HKLM_S, eh, "DoReport", D(0, "aus", 1, "an"));
            s.Get("Fehlerbericht-Dialog (ShowUI)", HKLM_S, eh, "ShowUI", D(0, "aus", 1, "an"));
            s.Get("Dr. Watson/Debugger (AeDebug Debugger)", HKLM_S,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AeDebug", "Debugger");
            s.Get("Dr. Watson Auto (AeDebug Auto)", HKLM_S,
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AeDebug", "Auto");
            const string cc = @"SYSTEM\CurrentControlSet\Control\CrashControl";
            s.Get("Bei Systemfehler neu starten (AutoReboot)", HKLM_S, cc, "AutoReboot", D(0, "nein", 1, "ja"));
            s.Get("Absturzabbild (CrashDumpEnabled)", HKLM_S, cc, "CrashDumpEnabled",
                D(0, "keins", 1, "vollständig", 2, "Kernel", 3, "klein"));
            const string sc = @"SOFTWARE\Microsoft\Security Center";
            foreach (var n in new[] { "AntiVirusDisableNotify", "FirewallDisableNotify", "UpdatesDisableNotify" })
                s.Get("Security Center " + n, HKLM_S, sc, n, D(0, "Meldung an", 1, "Meldung aus"));
            s.Get("Dienst wscsvc (Security Center) Starttyp", HKLM_S, SVCS + @"\wscsvc", "Start",
                D(2, "Auto", 3, "Manuell", 4, "Deaktiviert"));
            const string au = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update";
            s.Get("Automatische Updates (AUOptions)", HKLM_S, au, "AUOptions",
                D(1, "aus", 2, "benachrichtigen", 3, "laden", 4, "automatisch"));
            s.Get("Automatische Updates Policy (NoAutoUpdate)", HKLM_S,
                @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", D(0, "an", 1, "aus"));
            s.Get("Dienst wuauserv Starttyp", HKLM_S, SVCS + @"\wuauserv", "Start",
                D(2, "Auto", 3, "Manuell", 4, "Deaktiviert"));
            const string pw = @"SYSTEM\CurrentControlSet\Control\Power";
            s.Get("Ruhezustand aktiviert (HibernateEnabled)", HKLM_S, pw, "HibernateEnabled", D(0, "aus", 1, "an"));
            s.Get("Energieschema aktuell (PowerCfg)", HKCU_S, @"Control Panel\PowerCfg", "CurrentPowerPolicy");
            foreach (var ln in SplitLines(ShCmd("powercfg /list")))
                if (ln.Trim() != "" && !ln.Trim().StartsWith("---", StringComparison.Ordinal))
                    s.Add("powercfg /list", ln.Trim(), "powercfg");
            var q = SplitLines(ShCmd("powercfg /query")).Select(x => x.Trim()).Where(x => x != "").ToList();
            foreach (var ln in q.Take(40)) s.Add("powercfg /query", ln, "powercfg");
            return s;
        }

        static readonly Regex Spaces2 = new Regex(@"\s{2,}");

        static Sec SecNetwork()
        {
            var s = new Sec("Netzwerk und LAN");
            s.Get("Rechnername (ComputerName)", HKLM_S, @"SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName", "ComputerName");
            string tcp = SVCS + @"\Tcpip\Parameters";
            s.Get("DNS-Domäne (Tcpip Domain)", HKLM_S, tcp, "Domain");
            foreach (var ln in SplitLines(ShCmd("net config workstation")))
            {
                string low = ln.ToLowerInvariant();
                if (low.StartsWith("workstation domain", StringComparison.Ordinal) || low.StartsWith("full computer name", StringComparison.Ordinal)
                    || low.StartsWith("logon domain", StringComparison.Ordinal))
                {
                    var parts = Spaces2.Split(ln.Trim(), 2);
                    s.Add(parts[0], parts.Length > 1 ? parts[1] : "", "net config workstation");
                }
            }
            const string cards = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkCards";
            foreach (var sub in Reg.Subs(HKLM_S, cards))
            {
                var cv = Reg.Vals(HKLM_S, cards + "\\" + sub);
                object guid = G(cv, "ServiceName", null);
                if (!Py.Truthy(guid)) continue;
                string g = Str(guid);
                var iv = Reg.Vals(HKLM_S, tcp + @"\Interfaces" + "\\" + g);
                s.Add("Netzwerkkarte " + sub, G(cv, "Description", ""), "NetworkCards\\" + sub);
                s.Add("  DHCP aktiv (EnableDHCP)", G(iv, "EnableDHCP", null), "Tcpip\\Interfaces\\" + g + "\\EnableDHCP");
                foreach (var n in new[] { "IPAddress", "DhcpIPAddress", "SubnetMask", "DhcpSubnetMask", "DefaultGateway",
                    "DhcpDefaultGateway", "NameServer", "DhcpNameServer", "Domain", "DhcpServer" })
                {
                    object x = G(iv, n, null);
                    bool skip = x == null || (x is string && ((string)x == "" || (string)x == "0.0.0.0"));
                    if (!skip) s.Add("  " + n, x, "Tcpip\\Interfaces\\" + g + "\\" + n);
                }
            }
            foreach (var sub in Reg.Subs(HKCU_S, "Network"))
            {
                var v = Reg.Vals(HKCU_S, "Network\\" + sub);
                s.Add("Netzlaufwerk " + sub.ToUpperInvariant() + ":", G(v, "RemotePath", ""),
                    @"HKCU\Network\" + sub + " (Benutzer: " + Str(G(v, "UserName", "")) + ")");
            }
            foreach (var ln in SplitLines(ShCmd("net use")))
            {
                string t = ln.Trim();
                if (t.StartsWith("OK", StringComparison.Ordinal) || t.StartsWith("Disconnected", StringComparison.Ordinal)
                    || t.StartsWith("Unavailable", StringComparison.Ordinal))
                    s.Add("net use", string.Join(" ", ln.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)), "net use");
            }
            var shares = Reg.Vals(HKLM_S, SVCS + @"\lanmanserver\Shares");
            foreach (var n in Sorted(shares.Keys))
                s.Add("Freigabe " + n, shares[n], @"HKLM\" + SVCS + @"\lanmanserver\Shares");
            s.Get("NetBIOS über TCP/IP Dienst (NetBT) Starttyp", HKLM_S, SVCS + @"\NetBT", "Start");
            var fw = new Dictionary<string, string> { { "StandardProfile", "Standardprofil" }, { "DomainProfile", "Domänenprofil" } };
            foreach (var prof in Sorted(fw.Keys))
            {
                string bas = FWP + "\\" + prof;
                s.Get("Firewall " + fw[prof] + " aktiv (EnableFirewall)", HKLM_S, bas, "EnableFirewall", D(0, "aus", 1, "an"));
                s.Get("Firewall " + fw[prof] + " keine Ausnahmen", HKLM_S, bas, "DoNotAllowExceptions", D(0, "Ausnahmen erlaubt", 1, "keine"));
                s.Get("Firewall " + fw[prof] + " Meldungen aus", HKLM_S, bas, "DisableNotifications", D(0, "an", 1, "aus"));
                foreach (var ks in new[] { new[] { "Programm", @"\AuthorizedApplications\List" }, new[] { "Port", @"\GloballyOpenPorts\List" } })
                {
                    var v = Reg.Vals(HKLM_S, bas + ks[1]);
                    foreach (var n in Sorted(v.Keys))
                        s.Add("Firewall " + fw[prof] + " Ausnahme (" + ks[0] + ")", v[n], HKLM_S + "\\" + bas + ks[1]);
                }
            }
            s.Get("Dienst SharedAccess (Firewall/ICS) Starttyp", HKLM_S, SVCS + @"\SharedAccess", "Start",
                D(2, "Auto", 3, "Manuell", 4, "Deaktiviert"));
            const string ts = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
            s.Get("Remote Desktop gesperrt (fDenyTSConnections)", HKLM_S, ts, "fDenyTSConnections", D(0, "erlaubt", 1, "gesperrt"));
            s.Get("Remoteunterstützung (fAllowToGetHelp)", HKLM_S, ts, "fAllowToGetHelp", D(0, "aus", 1, "an"));
            s.Get("Dienst TermService Starttyp", HKLM_S, SVCS + @"\TermService", "Start",
                D(2, "Auto", 3, "Manuell", 4, "Deaktiviert"));
            return s;
        }

        class Svc { public string Name, Display, Image; public object Start; public long Type; }

        static List<Svc> CollectServices()
        {
            var o = new List<Svc>();
            foreach (var name in Reg.Subs(HKLM_S, SVCS))
            {
                var v = Reg.Vals(HKLM_S, SVCS + "\\" + name);
                object t = G(v, "Type", null);
                if (!(t is long) || (((long)t) & 0x30) == 0) continue;
                o.Add(new Svc { Name = name, Display = Str(G(v, "DisplayName", "")), Start = G(v, "Start", null),
                    Image = Str(G(v, "ImagePath", "")), Type = (long)t });
            }
            return o;
        }

        static bool StartIs(Svc x, long n) { return x.Start is long && (long)x.Start == n; }

        static List<string[]> StartupFolderItems()
        {
            var res = new List<string[]>();
            var paths = new List<string[]>();
            string allp = Environment.GetEnvironmentVariable("ALLUSERSPROFILE");
            string usr = Environment.GetEnvironmentVariable("USERPROFILE");
            if (!string.IsNullOrEmpty(allp)) paths.Add(new[] { "Alle Benutzer", Path.Combine(allp, @"Start Menu\Programs\Startup") });
            if (!string.IsNullOrEmpty(usr)) paths.Add(new[] { "Aktueller Benutzer", Path.Combine(usr, @"Start Menu\Programs\Startup") });
            foreach (var lp in paths)
            {
                try
                {
                    var names = new List<string>();
                    foreach (var f in Directory.GetFileSystemEntries(lp[1])) names.Add(Path.GetFileName(f));
                    foreach (var n in Sorted(names))
                        if (n.ToLowerInvariant() != "desktop.ini") res.Add(new[] { lp[0], Path.Combine(lp[1], n) });
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return res;
        }

        static Sec SecGaming()
        {
            var s = new Sec("Spiele-Tuning");
            s.Get("Visuelle Effekte (VisualFXSetting)", HKCU_S, EXPL + @"\VisualEffects", "VisualFXSetting",
                D(0, "von Windows wählen", 1, "beste Darstellung", 2, "beste Leistung", 3, "benutzerdefiniert"));
            var m = Reg.Vals(HKCU_S, @"Control Panel\Mouse");
            foreach (var n in new[] { "MouseSpeed", "MouseThreshold1", "MouseThreshold2", "MouseSensitivity", "SwapMouseButtons", "DoubleClickSpeed" })
            {
                string note = "";
                object mv = G(m, n, null);
                if (n == "MouseSpeed" && mv is string)
                {
                    string x = (string)mv;
                    if (x == "0") note = "Zeigerbeschleunigung aus";
                    else if (x == "1") note = "Beschleunigung niedrig";
                    else if (x == "2") note = "Beschleunigung an";
                }
                s.Add("Maus " + n, mv, @"HKCU\Control Panel\Mouse\" + n, note);
            }
            foreach (var n in new[] { "KeyboardDelay", "KeyboardSpeed" })
                s.Get("Tastatur " + n, HKCU_S, @"Control Panel\Keyboard", n);
            const string mm = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
            s.Get("Prefetch (EnablePrefetcher)", HKLM_S, mm + @"\PrefetchParameters", "EnablePrefetcher",
                D(0, "aus", 1, "Anwendungen", 2, "Start", 3, "beides (Standard)"));
            s.Get("Auslagerungsdatei (PagingFiles)", HKLM_S, mm, "PagingFiles");
            s.Get("Kernel im RAM halten (DisablePagingExecutive)", HKLM_S, mm, "DisablePagingExecutive", D(0, "nein", 1, "ja"));
            s.Get("Großer Systemcache (LargeSystemCache)", HKLM_S, mm, "LargeSystemCache", D(0, "nein", 1, "ja"));
            s.Get("Prioritätstrennung (Win32PrioritySeparation)", HKLM_S,
                @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation",
                D(2, "XP-Standard (Vordergrund-Boost 2:1)", 38, "0x26 (Vista-Standard)", 24, "0x18 (Server-Standard)"));
            var svcs = CollectServices();
            var startNames = new Dictionary<long, string> { { 0, "Boot" }, { 1, "System" }, { 2, "Auto" }, { 3, "Manuell" }, { 4, "Deaktiviert" } };
            var auto = Sorted(svcs.Where(x => StartIs(x, 2)).Select(x => x.Name));
            var dis = Sorted(svcs.Where(x => StartIs(x, 4)).Select(x => x.Name));
            int man = svcs.Count(x => StartIs(x, 3));
            s.Add("Dienste Auto (" + auto.Count + ")", string.Join(", ", auto.ToArray()), SVCS + "\\<Name>\\Start=2");
            s.Add("Dienste Deaktiviert (" + dis.Count + ")", string.Join(", ", dis.ToArray()), SVCS + "\\<Name>\\Start=4");
            s.Add("Dienste Manuell", (long)man, SVCS + "\\<Name>\\Start=3");
            foreach (var x in svcs)
            {
                string img = x.Image.ToLowerInvariant();
                if (!img.Contains("system32") && !img.Contains("svchost") && !img.Contains("%systemroot%"))
                {
                    string sn;
                    if (!(x.Start is long) || !startNames.TryGetValue((long)x.Start, out sn)) sn = Str(x.Start);
                    s.Add("Dienst von Dritten: " + x.Name, sn + ", " + x.Display + ", " + x.Image, SVCS + "\\" + x.Name);
                }
            }
            var runs = new[] {
                new[] { "HKLM Run", HKLM_S, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run" },
                new[] { "HKLM RunOnce", HKLM_S, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" },
                new[] { "HKLM RunServices", HKLM_S, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunServices" },
                new[] { "HKCU Run", HKCU_S, @"Software\Microsoft\Windows\CurrentVersion\Run" },
                new[] { "HKCU RunOnce", HKCU_S, @"Software\Microsoft\Windows\CurrentVersion\RunOnce" } };
            foreach (var rn in runs)
            {
                var v = Reg.Vals(rn[1], rn[2]);
                foreach (var n in Sorted(v.Keys))
                    s.Add("Autostart " + rn[0] + ": " + n, v[n], rn[1] + "\\" + rn[2] + "\\" + n);
            }
            foreach (var lp in StartupFolderItems())
                s.Add("Autostart-Ordner (" + lp[0] + ")", lp[1], "Startup-Ordner");
            return s;
        }

        static Sec SecHardware()
        {
            var s = new Sec("Grafik und Sound");
            foreach (var sub in Sorted(Reg.Subs(HKLM_S, CLASS_VIDEO)))
            {
                var vid = Reg.Vals(HKLM_S, CLASS_VIDEO + "\\" + sub);
                if (Py.Truthy(G(vid, "DriverDesc", null)))
                    s.Add("Grafik " + sub, Str(G(vid, "DriverDesc", null)) + ", " + Str(G(vid, "ProviderName", "")) + ", Treiber "
                        + Str(G(vid, "DriverVersion", "")) + " (" + Str(G(vid, "DriverDate", "")) + ")", CLASS_VIDEO + "\\" + sub);
            }
            var nv = Reg.Subs(HKCU_S, @"Software\NVIDIA Corporation\Global");
            s.Add("NVIDIA HKCU Global (Unterschlüssel)", string.Join(", ", nv.ToArray()), @"HKCU\Software\NVIDIA Corporation\Global");
            foreach (var sub in Sorted(Reg.Subs(HKLM_S, CLASS_MEDIA)))
            {
                if (!IsDigits(sub)) continue;
                var v = Reg.Vals(HKLM_S, CLASS_MEDIA + "\\" + sub);
                if (Py.Truthy(G(v, "DriverDesc", null)) && Str(G(v, "ProviderName", "")).ToLowerInvariant() != "microsoft")
                    s.Add("Sound " + sub, Str(G(v, "DriverDesc", null)) + ", " + Str(G(v, "ProviderName", "")) + ", "
                        + Str(G(v, "DriverVersion", "")), CLASS_MEDIA + "\\" + sub);
            }
            return s;
        }

        static readonly string[] SecIds = { "explorer", "design", "region", "users", "quiet", "network", "gaming", "hardware" };

        static Sec RunSection(string id)
        {
            switch (id)
            {
                case "explorer": return SecExplorer();
                case "design": return SecDesign();
                case "region": return SecRegion();
                case "users": return SecUsers();
                case "quiet": return SecQuiet();
                case "network": return SecNetwork();
                case "gaming": return SecGaming();
                default: return SecHardware();
            }
        }

        [Op("settings")]
        static J Settings(Conn c, Req r)
        {
            object only = r["only"];
            var res = new List<object>();
            foreach (var key in SecIds)
            {
                if (Py.Truthy(only))
                {
                    bool inOnly;
                    if (only is string) inOnly = ((string)only).Contains(key);
                    else if (only is List<object>) inOnly = ((List<object>)only).Any(x => x is string && (string)x == key);
                    else inOnly = false;
                    if (!inOnly) continue;
                }
                try
                {
                    var sec = RunSection(key);
                    res.Add(J.O("id", key, "title", sec.Title, "items", sec.Items));
                }
                catch (Exception e)
                {
                    res.Add(J.O("id", key, "title", key, "items", new List<object> {
                        J.O("k", "Fehler", "v", Server.ErrText(e), "reg", "", "note", "") }));
                }
            }
            return J.Ok("host", Environment.GetEnvironmentVariable("COMPUTERNAME") ?? "",
                "user", Environment.GetEnvironmentVariable("USERNAME") ?? "", "sections", res);
        }

        // ---------- productkey ----------

        const string XpKeyDigits = "BCDFGHJKMPQRTVWXY2346789";

        static string DecodeXpKey(byte[] raw)
        {
            var d = new List<int>();
            for (int i = 52; i < 67 && i < raw.Length; i++) d.Add(raw[i]);
            if (d.Count < 15) throw new OpError("DigitalProductId zu kurz");
            var outp = new StringBuilder();
            for (int n = 0; n < 25; n++)
            {
                long cur = 0;
                for (int j = 14; j >= 0; j--)
                {
                    cur = cur * 256 + d[j];
                    d[j] = (int)(cur / 24);
                    cur = cur % 24;
                }
                outp.Insert(0, XpKeyDigits[(int)cur]);
            }
            string o = outp.ToString();
            var parts = new List<string>();
            for (int i = 0; i < 25; i += 5) parts.Add(o.Substring(i, 5));
            return string.Join("-", parts.ToArray());
        }

        [Op("productkey")]
        static J ProductKey(Conn c, Req r)
        {
            int rc;
            var k = OpenRc(Reg.HKLM, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", out rc);
            if (k == null) throw OsErr(rc);
            RegValue v;
            using (k) v = k.Query("DigitalProductId");
            if (v == null) throw OsErr(2);
            var raw = v.Data as byte[];
            if (raw == null) throw new OpError("TypeError", "DigitalProductId ist kein Binärwert");
            string key = DecodeXpKey(raw);
            bool ok = Regex.IsMatch(key, "^([" + XpKeyDigits + "]{5}-){4}[" + XpKeyDigits + "]{5}$");
            Log.Write("  productkey gelesen (wird nicht geloggt)");
            return J.Ok("key", key, "format_ok", ok);
        }

        static OpError OsErr(int rc)
        {
            string t = rc == 2 || rc == 3 ? "FileNotFoundError" : rc == 5 ? "PermissionError" : "OSError";
            return new OpError(t, WinErr(rc));
        }
    }
}
