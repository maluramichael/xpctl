using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace XpServer
{
    /// <summary>Dateien, Prozesse, Discs, Installer: ls, cat, ps, kill, hash, copy, rm, disc, setupapi, install, uninstall.</summary>
    public static class FilesOps
    {
        static readonly string[] NoKill = { "explorer.exe", "csrss.exe", "winlogon.exe", "lsass.exe", "services.exe",
            "smss.exe", "svchost.exe", "system", "python.exe", "pythonw.exe" };

        // ---------- Python-Nachbauten ----------

        /// <summary>repr() eines Strings.</summary>
        static string PyRepr(string s)
        {
            char q = s.IndexOf('\'') >= 0 && s.IndexOf('"') < 0 ? '"' : '\'';
            var sb = new StringBuilder();
            sb.Append(q);
            foreach (char c in s)
            {
                if (c == q || c == '\\') { sb.Append('\\').Append(c); }
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 32 || c == 127) sb.Append("\\x" + ((int)c).ToString("x2"));
                else sb.Append(c);
            }
            sb.Append(q);
            return sb.ToString();
        }

        static bool Sep(char c) { return c == '\\' || c == '/'; }

        /// <summary>ntpath.join(a, b) (für normale Fälle).</summary>
        static string Join(string a, string b)
        {
            if (b.Length > 0 && (Sep(b[0]) || (b.Length > 1 && b[1] == ':'))) return b;
            if (a.Length == 0) return b;
            char l = a[a.Length - 1];
            if (!Sep(l) && l != ':') return a + "\\" + b;
            return a + b;
        }

        /// <summary>ntpath.basename.</summary>
        static string BaseName(string p)
        {
            int i = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
            int d = p.Length > 1 && p[1] == ':' ? 2 : 0;
            return p.Substring(Math.Max(i + 1, d));
        }

        /// <summary>ntpath.dirname.</summary>
        static string DirName(string p)
        {
            int i = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
            int d = p.Length > 1 && p[1] == ':' ? 2 : 0;
            int cut = Math.Max(i + 1, d);
            string head = p.Substring(0, cut);
            string trimmed = head.TrimEnd('\\', '/');
            return trimmed.Length > 0 ? trimmed : head;
        }

        static string Lower(string s) { return s.ToLowerInvariant(); }

        /// <summary>fnmatch.translate (Python 3.4).</summary>
        static Regex FnRegex(string pat)
        {
            var res = new StringBuilder();
            int i = 0, n = pat.Length;
            while (i < n)
            {
                char c = pat[i++];
                if (c == '*') res.Append(".*");
                else if (c == '?') res.Append('.');
                else if (c == '[')
                {
                    int j = i;
                    if (j < n && pat[j] == '!') j++;
                    if (j < n && pat[j] == ']') j++;
                    while (j < n && pat[j] != ']') j++;
                    if (j >= n) res.Append("\\[");
                    else
                    {
                        string stuff = pat.Substring(i, j - i).Replace("\\", "\\\\");
                        i = j + 1;
                        if (stuff[0] == '!') stuff = "^" + stuff.Substring(1);
                        else if (stuff[0] == '^') stuff = "\\" + stuff;
                        res.Append('[').Append(stuff).Append(']');
                    }
                }
                else res.Append(Regex.Escape(c.ToString()));
            }
            return new Regex("^(?:" + res + ")\\z", RegexOptions.Singleline);
        }

        static readonly Dictionary<string, Regex> FnCache = new Dictionary<string, Regex>();

        static bool Match(string name, IList<string> patterns)
        {
            if (patterns == null || patterns.Count == 0) return true;
            string n = Lower(name).Replace('/', '\\');
            foreach (var p in patterns)
            {
                string key = Lower(p).Replace('/', '\\');
                Regex rx;
                lock (FnCache)
                {
                    if (!FnCache.TryGetValue(key, out rx)) { rx = FnRegex(key); FnCache[key] = rx; }
                }
                if (rx.IsMatch(n)) return true;
            }
            return false;
        }

        static string Stamp(DateTime utc)
        {
            return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        /// <summary>str.splitlines().</summary>
        static List<string> SplitLines(string s)
        {
            var r = new List<string>();
            int start = 0, n = s.Length;
            for (int i = 0; i < n; i++)
            {
                char c = s[i];
                if (c == '\r' || c == '\n' || c == '\v' || c == '\f' || c == '\x1c' || c == '\x1d' || c == '\x1e'
                    || c == '\x85' || c == '\u2028' || c == '\u2029')
                {
                    r.Add(s.Substring(start, i - start));
                    if (c == '\r' && i + 1 < n && s[i + 1] == '\n') i++;
                    start = i + 1;
                }
            }
            if (start < n) r.Add(s.Substring(start));
            return r;
        }

        /// <summary>_decode: utf-8, dann cp1252 (streng), sonst latin1.</summary>
        static string Decode(byte[] data)
        {
            try { return new UTF8Encoding(false, true).GetString(data); } catch (ArgumentException) { }
            bool ok = true;
            foreach (byte b in data)
                if (b == 0x81 || b == 0x8D || b == 0x8F || b == 0x90 || b == 0x9D) { ok = false; break; }
            if (ok) return Encoding.GetEncoding(1252).GetString(data);
            return Encoding.GetEncoding(28591).GetString(data);
        }

        static string WinMsg(int code) { return new Win32Exception(code).Message; }

        /// <summary>Python-Fehlertext für os.stat/os.remove: "[WinError n] Text: 'pfad'".</summary>
        static OpError WinErr(int code, string path)
        {
            string t = code == 5 || code == 32 ? "PermissionError" : (code == 2 || code == 3 ? "FileNotFoundError" : "OSError");
            return new OpError(t, string.Format("[WinError {0}] {1}: {2}", code, WinMsg(code), PyRepr(path)));
        }

        static int MissingCode(string path)
        {
            string p = DirName(path);
            return p.Length == 0 || Directory.Exists(p) ? 2 : 3;
        }

        /// <summary>Text eines open()-Fehlers: "[Errno 2] No such file or directory: 'pfad'".</summary>
        static string OpenErr(Exception e, string path)
        {
            if (e is FileNotFoundException || e is DirectoryNotFoundException)
                return "[Errno 2] No such file or directory: " + PyRepr(path);
            if (e is UnauthorizedAccessException || e is IOException)
                return "[Errno 13] Permission denied: " + PyRepr(path);
            return e.Message;
        }

        static string OpenErrType(Exception e)
        {
            if (e is FileNotFoundException || e is DirectoryNotFoundException) return "FileNotFoundError";
            if (e is UnauthorizedAccessException || e is IOException) return "PermissionError";
            return "OSError";
        }

        static FileStream OpenRead(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        static string Md5(string path)
        {
            using (var f = OpenRead(path))
            using (var h = MD5.Create()) return Server.Hex(h.ComputeHash(f));
        }

        static byte[] ReadAll(FileStream f)
        {
            var ms = new MemoryStream();
            var buf = new byte[65536];
            int n;
            while ((n = f.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
            return ms.ToArray();
        }

        static List<object> Objs(IEnumerable<string> l) { var o = new List<object>(); foreach (var s in l) o.Add(s); return o; }

        // ---------- ls ----------

        class LsCtx
        {
            public List<string> Pats;
            public long Depth, Limit, Total, BaseDepth;
            public bool FilesOnly;
            public List<object> Out = new List<object>();
        }

        static long CountBs(string s) { long n = 0; foreach (char c in s) if (c == '\\') n++; return n; }

        static void Split(string dir, List<string> dirs, List<string> files)
        {
            foreach (var e in new DirectoryInfo(dir).GetFileSystemInfos())
            {
                if ((e.Attributes & FileAttributes.Directory) != 0) dirs.Add(e.Name); else files.Add(e.Name);
            }
        }

        static bool LsWalk(LsCtx x, string bas)
        {
            long level = CountBs(bas.TrimEnd('\\')) - x.BaseDepth;
            bool stopDeeper = x.Depth >= 0 && level >= x.Depth;
            var dirs = new List<string>();
            var files = new List<string>();
            try { Split(bas, dirs, files); }
            catch { return false; }
            dirs = Py.SortBy(dirs, Lower);
            files = Py.SortBy(files, Lower);
            foreach (var d in dirs)
                if (!x.FilesOnly && Match(d, x.Pats)) x.Out.Add(J.O("path", Join(bas, d), "dir", true));
            foreach (var f in files)
            {
                if (!Match(f, x.Pats)) continue;
                string p = Join(bas, f);
                try
                {
                    var fi = new FileInfo(p);
                    long size = fi.Length;
                    x.Out.Add(J.O("path", p, "size", size, "mtime", Stamp(fi.LastWriteTimeUtc), "dir", false));
                    x.Total += size;
                }
                catch (Exception e)
                {
                    x.Out.Add(J.O("path", p, "error", e.Message, "dir", false));
                }
            }
            if (x.Out.Count >= x.Limit) return true;
            if (!stopDeeper)
                foreach (var d in dirs)
                    if (LsWalk(x, Join(bas, d))) return true;
            return false;
        }

        [Op("ls")]
        static J Ls(Conn c, Req r)
        {
            string root = r.Need("path");
            string stop = root.Length >= 2 && root[1] == ':' ? Common.RipGuard(r, root.Substring(0, 2)) : null;
            if (stop != null) return J.Err(stop);
            var x = new LsCtx();
            x.Pats = r.Strs("patterns");
            x.Depth = r.Has("recurse") ? r.Int("recurse") : 0;
            long limit = r.Int("limit");
            x.Limit = limit != 0 ? limit : 2000;
            x.FilesOnly = r.Bool("files_only");
            if (File.Exists(root))
            {
                var fi = new FileInfo(root);
                var l = new List<object>();
                l.Add(J.O("path", root, "size", fi.Length, "mtime", Stamp(fi.LastWriteTimeUtc), "dir", false));
                return J.Ok("entries", l, "total", fi.Length, "truncated", false);
            }
            if (!Directory.Exists(root)) return J.Err("gibt es nicht: " + root);
            x.BaseDepth = CountBs(root.TrimEnd('\\'));
            LsWalk(x, root);
            var outL = x.Out.Count > x.Limit ? x.Out.GetRange(0, (int)x.Limit) : x.Out;
            return J.Ok("entries", outL, "total", x.Total, "truncated", x.Out.Count >= x.Limit);
        }

        // ---------- cat ----------

        static int Clamp(long v, int n) { return (int)Math.Max(0, Math.Min(n, v)); }

        [Op("cat")]
        static J Cat(Conn c, Req r)
        {
            string path = r.Need("path");
            long size;
            if (File.Exists(path)) size = new FileInfo(path).Length;
            else if (Directory.Exists(path)) size = 0;
            else throw WinErr(MissingCode(path), path);
            const long cap = 4L * 1024 * 1024;
            byte[] data;
            try
            {
                using (var f = OpenRead(path))
                {
                    if (size > cap) f.Seek(size - cap, SeekOrigin.Begin);
                    data = ReadAll(f);
                }
            }
            catch (Exception e)
            {
                throw new OpError(OpenErrType(e), OpenErr(e, path));
            }
            var lines = SplitLines(Decode(data));
            string g = r.Str("grep");
            if (Py.Truthy(r["grep"]) && g != null)
            {
                var o = new List<string>();
                if (g.Length > 2 && g.StartsWith("/") && g.EndsWith("/"))
                {
                    var rx = new Regex(g.Substring(1, g.Length - 2), RegexOptions.IgnoreCase);
                    for (int i = 0; i < lines.Count; i++) if (rx.IsMatch(lines[i])) o.Add((i + 1) + ": " + lines[i]);
                }
                else
                {
                    string gl = Lower(g);
                    for (int i = 0; i < lines.Count; i++) if (Lower(lines[i]).Contains(gl)) o.Add((i + 1) + ": " + lines[i]);
                }
                lines = o;
            }
            if (r.Bool("tail"))
            {
                long t = r.Int("tail");
                int st = Clamp(t > 0 ? lines.Count - t : -t, lines.Count);
                lines = lines.GetRange(st, lines.Count - st);
            }
            if (r.Bool("head"))
            {
                long h = r.Int("head");
                int en = Clamp(h < 0 ? lines.Count + h : h, lines.Count);
                lines = lines.GetRange(0, en);
            }
            if (lines.Count > 20000) lines = lines.GetRange(0, 20000);
            return J.Ok("size", size, "cut", size > cap, "lines", Objs(lines));
        }

        // ---------- Prozesse ----------

        static List<J> Processes()
        {
            var psi = new ProcessStartInfo("tasklist", "/fo csv /nh");
            var res = Proc.Run(psi, 30);
            var procs = new List<J>();
            foreach (var line in SplitLines(Proc.Oem.GetString(res.Out)))
            {
                var cols = new List<string>();
                foreach (var part in line.Split(new[] { "\",\"" }, StringSplitOptions.None)) cols.Add(part.Trim('"'));
                if (cols.Count >= 5)
                {
                    var digits = Regex.Replace(cols[4], "[^0-9]", "");
                    procs.Add(J.O("name", cols[0].Trim('"'), "pid", long.Parse(cols[1]),
                        "mem_kb", digits.Length > 0 ? long.Parse(digits) : 0L));
                }
            }
            return procs;
        }

        [Op("ps")]
        static J Ps(Conn c, Req r)
        {
            string filt = Lower(r.Or("filter", ""));
            var procs = new List<J>();
            foreach (var p in Processes()) if (Lower((string)p["name"]).Contains(filt)) procs.Add(p);
            if (r.Bool("cmdline") && procs.Count > 0)
            {
                var qs = new List<string>();
                for (int i = 0; i < procs.Count && i < 40; i++) qs.Add("ProcessId=" + procs[i]["pid"]);
                var psi = new ProcessStartInfo("wmic", "process where \"" + string.Join(" or ", qs.ToArray()) +
                    "\" get ProcessId,CommandLine /format:list");
                var res = Proc.Run(psi, 60);
                string cur = null;
                foreach (var line in Proc.Oem.GetString(res.Out).Replace("\r", "").Split('\n'))
                {
                    if (line.StartsWith("CommandLine=")) cur = line.Substring(12);
                    else if (line.StartsWith("ProcessId="))
                    {
                        string ps = line.Substring(10);
                        long pid = ps.Length > 0 ? long.Parse(ps) : 0;
                        foreach (var p in procs) if ((long)p["pid"] == pid) p["cmdline"] = cur ?? "";
                        cur = null;
                    }
                }
            }
            return J.Ok("procs", new List<object>(procs.ToArray()));
        }

        static bool IsDigits(string s)
        {
            if (s.Length == 0) return false;
            foreach (char c in s) if (!char.IsDigit(c)) return false;
            return true;
        }

        [Op("kill")]
        static J Kill(Conn c, Req r)
        {
            if (!r.Raw.ContainsKey("targets")) throw new OpError("KeyError", "'targets'");
            var targets = new List<J>();
            var procs = Processes();
            foreach (var t0 in r.List("targets"))
            {
                string t = Py.Repr(t0);
                if (IsDigits(t))
                {
                    long pid = long.Parse(t);
                    foreach (var p in procs) if ((long)p["pid"] == pid) targets.Add(p);
                }
                else
                    foreach (var p in procs) if (Lower((string)p["name"]) == Lower(t)) targets.Add(p);
            }
            if (targets.Count == 0) return J.Err("kein passender Prozess");
            bool force = r.Bool("force");
            var blocked = new List<string>();
            foreach (var p in targets)
                if (Array.IndexOf(NoKill, Lower((string)p["name"])) >= 0 && !force) blocked.Add(p["name"] + " " + p["pid"]);
            if (blocked.Count > 0) return J.Err("geschützt (force nötig): " + string.Join(", ", blocked.ToArray()));
            var res = new List<J>();
            bool tree = r.Bool("tree");
            foreach (var p in targets)
            {
                var psi = new ProcessStartInfo("taskkill", "/F /PID " + p["pid"] + (tree ? " /T" : ""));
                var rr = Proc.Run(psi, 60);
                res.Add(J.O("name", p["name"], "pid", p["pid"], "ok", rr.Exit == 0));
            }
            Thread.Sleep(1000);
            var alive = new Dictionary<long, bool>();
            foreach (var p in Processes()) alive[(long)p["pid"]] = true;
            bool all = true;
            foreach (var x in res) { x["still_running"] = alive.ContainsKey((long)x["pid"]); if (!(bool)x["ok"]) all = false; }
            return J.O("ok", all, "killed", new List<object>(res.ToArray()),
                "error", all ? null : "nicht alle beendet (hängt evtl. im Kernel-I/O)");
        }

        // ---------- hash ----------

        static HashAlgorithm NewHash(string algo)
        {
            switch (Lower(algo))
            {
                case "md5": return MD5.Create();
                case "sha1": return SHA1.Create();
                case "sha256": return SHA256.Create();
                case "sha384": return SHA384.Create();
                case "sha512": return SHA512.Create();
            }
            throw new ArgumentException("unsupported hash type " + algo);
        }

        [Op("hash")]
        static J Hash(Conn c, Req r)
        {
            string algo = r.Or("algo", "md5");
            if (!r.Raw.ContainsKey("paths")) throw new OpError("KeyError", "'paths'");
            var o = new J();
            foreach (var p in r.Strs("paths"))
            {
                try
                {
                    using (var h = NewHash(algo))
                    using (var f = OpenRead(p)) o[p] = Server.Hex(h.ComputeHash(f));
                }
                catch (ArgumentException e) { o[p] = "Fehler: " + e.Message; }
                catch (Exception e) { o[p] = "Fehler: " + OpenErr(e, p); }
            }
            return J.Ok("algo", algo, "hashes", o);
        }

        // ---------- copy ----------

        static void WalkFiles(string dir, string rel, List<string[]> o)
        {
            string full = rel.Length == 0 ? dir : Join(dir, rel);
            var files = new List<string>();
            var dirs = new List<string>();
            try { Split(full, dirs, files); }
            catch { return; }
            foreach (var f in files) o.Add(new[] { Join(full, f), rel.Length == 0 ? f : Join(rel, f) });
            foreach (var d in dirs) WalkFiles(dir, rel.Length == 0 ? d : Join(rel, d), o);
        }

        [Op("copy")]
        static J Copy(Conn c, Req r)
        {
            string src = r.Need("src"), dst = r.Need("dst");
            if (Directory.Exists(dst) || dst.EndsWith("\\"))
            {
                string b = BaseName(src.TrimEnd('\\'));
                dst = Join(dst, b.Length > 0 ? b : "disc");
            }
            var pairs = new List<string[]>();
            if (Directory.Exists(src))
            {
                var tmp = new List<string[]>();
                WalkFiles(src, "", tmp);
                foreach (var t in tmp) pairs.Add(new[] { t[0], Join(dst, t[1]) });
            }
            else pairs.Add(new[] { src, dst });
            var done = new List<object>();
            var failed = new List<object>();
            long total = 0;
            var start = DateTime.UtcNow;
            bool verify = r.Bool("verify");
            bool force = r.Bool("force");   // schreibgeschützte Ziele überschreiben (Dateien von CD-Installationen)
            foreach (var pr in pairs)
            {
                string s = pr[0], d = pr[1];
                try
                {
                    if (!File.Exists(s))
                    {
                        failed.Add(J.O("src", s, "error", "[Errno 2] No such file or directory: " + PyRepr(s)));
                        continue;
                    }
                    string parent = DirName(d);
                    if (parent.Length > 0 && !Directory.Exists(parent)) Directory.CreateDirectory(parent);
                    if (Lower(Path.GetFullPath(s)) == Lower(Path.GetFullPath(d)))
                    {
                        failed.Add(J.O("src", s, "error", PyRepr(s) + " and " + PyRepr(d) + " are the same file"));
                        continue;
                    }
                    if (force && File.Exists(d))
                    {
                        var at = File.GetAttributes(d);
                        if ((at & FileAttributes.ReadOnly) != 0) File.SetAttributes(d, at & ~FileAttributes.ReadOnly);
                    }
                    File.Copy(s, d, true);
                    long size = new FileInfo(d).Length;
                    total += size;
                    var item = J.O("src", s, "size", size);
                    if (verify)
                    {
                        string a = Md5(s), bb = Md5(d);
                        item["md5"] = a;
                        if (a != bb)
                        {
                            failed.Add(J.O("src", s, "error", "md5 weicht ab (" + a + " / " + bb + ")"));
                            continue;
                        }
                    }
                    done.Add(item);
                }
                catch (Exception e)
                {
                    string msg = e is UnauthorizedAccessException ? "[Errno 13] Permission denied: " + PyRepr(d) :
                        e is DirectoryNotFoundException ? "[Errno 2] No such file or directory: " + PyRepr(d) : e.Message;
                    failed.Add(J.O("src", s, "error", msg));
                }
            }
            return J.O("ok", failed.Count == 0, "copied", done, "failed", failed, "bytes", total,
                "seconds", (long)(DateTime.UtcNow - start).TotalSeconds, "dst", dst,
                "error", failed.Count == 0 ? null : failed.Count + " Datei(en) nicht kopiert");
        }

        // ---------- rm ----------

        static void ClearReadOnly(string dir)
        {
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var at = File.GetAttributes(f);
                if ((at & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, at & ~FileAttributes.ReadOnly);
            }
            foreach (var d in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories))
            {
                var di = new DirectoryInfo(d);
                if ((di.Attributes & FileAttributes.ReadOnly) != 0) di.Attributes &= ~FileAttributes.ReadOnly;
            }
        }

        [Op("rm")]
        static J Rm(Conn c, Req r)
        {
            if (!r.Raw.ContainsKey("paths")) throw new OpError("KeyError", "'paths'");
            var removed = new List<object>();
            var errors = new List<string>();
            string au = Common.AllUsersDesktop, me = Common.MyDesktop;
            var roots = new List<string>();
            foreach (var p in Cfg.Strs("rm_roots")) roots.Add(Lower(p.TrimEnd('\\')) + "\\");
            var safe = new List<string>(roots);
            safe.Add(Lower(au) + "\\");
            safe.Add(Lower(me) + "\\");
            bool force = r.Bool("force");
            foreach (var p in r.Strs("paths"))
            {
                string full = Path.GetFullPath(p);
                if (full.Length > 3) full = full.TrimEnd('\\');
                bool ok = false;
                string fl = Lower(full);
                foreach (var s in safe) if (fl.StartsWith(s, StringComparison.Ordinal)) ok = true;
                if (!force && !ok)
                {
                    errors.Add(p + ": außerhalb der freigegebenen Ordner (" + (roots.Count > 0 ? "force nötig" :
                        "nicht konfiguriert: \"rm_roots\" in " + Cfg.Name + ", sonst force") + ")");
                    continue;
                }
                try
                {
                    if (Directory.Exists(full))
                    {
                        if (!r.Bool("recurse")) { errors.Add(p + ": Ordner, recurse nötig"); continue; }
                        ClearReadOnly(full);   // Dateien von CD-Installationen sind oft schreibgeschützt
                        Directory.Delete(full, true);
                    }
                    else
                    {
                        if (!File.Exists(full)) throw WinErr(MissingCode(full), full);
                        File.Delete(full);
                    }
                    removed.Add(full);
                }
                catch (OpError e) { errors.Add(p + ": " + e.Message); }
                catch (UnauthorizedAccessException) { errors.Add(p + ": " + WinErr(5, full).Message); }
                catch (IOException e) { errors.Add(p + ": " + e.Message); }
            }
            return J.O("ok", errors.Count == 0, "removed", removed,
                "error", errors.Count > 0 ? string.Join("; ", errors.ToArray()) : null);
        }

        // ---------- disc ----------

        static readonly KeyValuePair<string, string[]>[] ProtectFiles = {
            new KeyValuePair<string, string[]>("SafeDisc 1", new[] { "00000001.tmp", "*.icd", "clcd16.dll", "clcd32.dll", "clokspl.exe", "dplayerx.dll" }),
            new KeyValuePair<string, string[]>("SafeDisc 2+", new[] { "00000002.tmp", "secdrv.sys", "drvmgt.dll" }),
            new KeyValuePair<string, string[]>("SecuROM", new[] { "cms16.dll", "cms_95.dll", "cms_nt.dll", "cms32_95.dll", "cms32_nt.dll", "*.securom" }),
            new KeyValuePair<string, string[]>("LaserLock", new[] { "laserlok.*", "nomouse.*", "*.laserlock" }),
            new KeyValuePair<string, string[]>("StarForce", new[] { "protect.dll", "protect.exe", "protect.x64", "protect.x86" }),
            new KeyValuePair<string, string[]>("TAGES", new[] { "tages*.*", "*.tagesprotection" }),
            new KeyValuePair<string, string[]>("CD-Cops", new[] { "cdcops.dll", "*.gz_", "*.w_x" }),
        };

        static readonly Dictionary<string, string> ProtectSections = new Dictionary<string, string> {
            { "stxt774", "SafeDisc 2+" }, { "stxt371", "SafeDisc 2+" }, { ".securom", "SecuROM" },
            { ".laser", "LaserLock" }, { ".icd", "SafeDisc" }, { "bitarts", "Crunch/Bitarts" } };

        static List<object> PeSections(string path)
        {
            byte[] d = new byte[4096];
            int len = 0, n0;
            using (var f = OpenRead(path))
                while (len < d.Length && (n0 = f.Read(d, len, d.Length - len)) > 0) len += n0;
            var o = new List<object>();
            if (len < 0x40 || d[0] != (byte)'M' || d[1] != (byte)'Z') { o.Add("(keine Exe)"); return o; }
            int p = (int)BitConverter.ToUInt32(d, 0x3C);
            if (p + 24 > len || d[p] != 'P' || d[p + 1] != 'E' || d[p + 2] != 0 || d[p + 3] != 0) { o.Add("(DOS-Exe)"); return o; }
            int n = BitConverter.ToUInt16(d, p + 6);
            int so = p + 24 + BitConverter.ToUInt16(d, p + 20);
            for (int i = 0; i < n; i++)
            {
                int a = so + 40 * i;
                var sb = new StringBuilder();
                for (int k = a; k < a + 8 && k < len; k++) sb.Append((char)d[k]);
                o.Add(sb.ToString().TrimEnd('\0'));
            }
            return o;
        }

        static void DiscWalk(int rootLen, string bas, List<string[]> files)
        {
            string rel = bas.Length > rootLen ? bas.Substring(rootLen) : "";
            int level = (int)CountBs(rel) + (rel.Length > 0 ? 1 : 0);
            var fl = new List<string>();
            var dl = new List<string>();
            try { Split(bas, dl, fl); }
            catch { return; }
            foreach (var f in fl) files.Add(new[] { bas, f, level.ToString() });
            if (level >= 3) return;
            foreach (var d in dl) DiscWalk(rootLen, Join(bas, d), files);
        }

        static void AddHit(J hits, string kind, string text)
        {
            if (!hits.ContainsKey(kind)) hits[kind] = new List<object>();
            ((List<object>)hits[kind]).Add(text);
        }

        [Op("disc")]
        static J Disc(Conn c, Req r)
        {
            string drive = Common.CdDrive(r);
            string stop = Common.RipGuard(r, drive);
            if (stop != null) return J.Err(stop);
            string label = Common.VolLabel(drive);
            if (label == null) return J.Err("kein Medium in " + drive);
            string root = drive + "\\";
            var names = new List<string>();
            foreach (var e in new DirectoryInfo(root).GetFileSystemInfos()) names.Add(e.Name);
            var top = Objs(Py.SortBy(names, Lower));
            int rootLen = root.Length;
            var hits = new J();
            var exes = new List<string>();
            var all = new List<string[]>();
            DiscWalk(rootLen, root, all);
            foreach (var t in all)
            {
                string f = t[1];
                int level = int.Parse(t[2]);
                foreach (var kv in ProtectFiles)
                    if (Match(f, kv.Value)) AddHit(hits, kv.Key, Join(t[0], f).Substring(rootLen));
                if (Lower(f).EndsWith(".exe") && level <= 1) exes.Add(Join(t[0], f));
            }
            var secs = new J();
            for (int i = 0; i < exes.Count && i < 30; i++)
            {
                string e = exes[i];
                string rel = e.Substring(rootLen);
                try
                {
                    var sl = PeSections(e);
                    secs[rel] = sl;
                    foreach (string s in sl)
                    {
                        string k;
                        if (ProtectSections.TryGetValue(Lower(s), out k)) AddHit(hits, k, rel + " [" + s + "]");
                    }
                }
                catch (Exception ex) { secs[rel] = "Fehler: " + OpenErr(ex, e); }
            }
            return J.Ok("drive", drive, "label", label, "root", top, "protection", hits, "sections", secs);
        }

        // ---------- setupapi ----------

        [Op("setupapi")]
        static J SetupApi(Conn c, Req r)
        {
            string path = Join(Common.SysRoot(), "setupapi.log");
            byte[] data;
            try
            {
                using (var f = OpenRead(path))
                {
                    f.Seek(Math.Max(0, f.Length - 2L * 1024 * 1024), SeekOrigin.Begin);
                    data = ReadAll(f);
                }
            }
            catch (Exception e)
            {
                throw new OpError(OpenErrType(e), OpenErr(e, path));
            }
            string text = Decode(data);
            var blocks = new List<string>(Regex.Split(text, @"\r?\n(?=\[\d{4}/\d\d/\d\d )"));
            string g = Lower(r.Or("grep", ""));
            if (g.Length > 0) blocks = blocks.FindAll(b => Lower(b).Contains(g));
            long last = r.Int("last");
            if (last == 0) last = 5;
            int st = Clamp(last > 0 ? blocks.Count - last : -last, blocks.Count);
            return J.Ok("blocks", Objs(blocks.GetRange(st, blocks.Count - st)));
        }

        // ---------- install / uninstall ----------

        static bool Has(byte[] hay, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= hay.Length; i++)
            {
                if (hay[i] != needle[0]) continue;
                int k = 1;
                while (k < needle.Length && hay[i + k] == needle[k]) k++;
                if (k == needle.Length) return true;
            }
            return false;
        }

        static string InstallerKind(string path)
        {
            if (Lower(path).EndsWith(".msi")) return "msi";
            byte[] head;
            using (var f = OpenRead(path))
            {
                var buf = new byte[4 * 1024 * 1024];
                int got = 0, n;
                while (got < buf.Length && (n = f.Read(buf, got, buf.Length - got)) > 0) got += n;
                head = new byte[got];
                Array.Copy(buf, head, got);
            }
            var a = Encoding.ASCII;
            var inno16 = new byte[] { (byte)'I', 0, (byte)'n', 0, (byte)'n', 0, (byte)'o', 0, (byte)' ', 0,
                (byte)'S', 0, (byte)'e', 0, (byte)'t', 0, (byte)'u', 0, (byte)'p' };
            if (Has(head, a.GetBytes("Inno Setup"))) return "inno";
            if (Has(head, inno16)) return "inno";
            if (Has(head, a.GetBytes("Nullsoft"))) return "nsis";
            if (Has(head, a.GetBytes("NullsoftInst"))) return "nsis";
            if (Has(head, a.GetBytes("InstallShield"))) return "installshield";
            return "unbekannt";
        }

        static long CountFiles(string dir)
        {
            long n = 0;
            try
            {
                foreach (var e in new DirectoryInfo(dir).GetFileSystemInfos())
                    if ((e.Attributes & FileAttributes.Directory) != 0) n += CountFiles(e.FullName); else n++;
            }
            catch { }
            return n;
        }

        [Op("install")]
        static J Install(Conn c, Req r)
        {
            string setup = r.Need("setup");
            string target = r.Need("dir").TrimEnd('\\');
            bool detect = r.Bool("detect_only");
            if (!detect && (Directory.Exists(target) || File.Exists(target)) && !r.Bool("force"))
            {
                if (!Directory.Exists(target))
                    throw new OpError("NotADirectoryError", "[WinError 267] Der Verzeichnisname ist ungültig: " + PyRepr(target));
                if (new DirectoryInfo(target).GetFileSystemInfos().Length > 0)
                    return J.Err("Zielordner existiert und ist nicht leer: " + target + " (fremde Dateien? sonst --force)");
            }
            if (!File.Exists(setup) && !Directory.Exists(setup)) return J.Err("Installer nicht gefunden: " + setup);
            string kind = r.Or("kind", "auto");
            if (kind == "auto") kind = InstallerKind(setup);
            if (detect) return J.Ok("kind", kind);
            string log = r.Or("log", null);
            if (log == null) log = "C:\\temp\\install-" + Regex.Replace(BaseName(target), "[^A-Za-z0-9]+", "_") + ".log";
            string ld = DirName(log);
            if (!Directory.Exists(ld)) Directory.CreateDirectory(ld);
            if (File.Exists(log)) File.Delete(log);
            string extra = r.Or("args", "");
            string cmd;
            if (kind == "inno")
                cmd = "\"" + setup + "\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LANG=" + r.Or("lang", "german") +
                    " \"/DIR=" + target + "\" \"/LOG=" + log + "\" " + extra;
            else if (kind == "nsis")
                cmd = "\"" + setup + "\" /S " + extra + " /D=" + target;
            else if (kind == "msi")
                cmd = "msiexec /i \"" + setup + "\" /qb /norestart TARGETDIR=\"" + target + "\" INSTALLDIR=\"" + target +
                    "\" /l*v \"" + log + "\" " + extra;
            else
                return J.Ok("status", "unbekannt", "kind", kind,
                    "hint", "Installer-Typ nicht erkannt: interaktiv per run --detach + ui/press");
            var start = DateTime.UtcNow;
            double timeout = r.Has("timeout") ? r.Dbl("timeout") : 1800;
            int code;
            using (var p = Process.Start(Proc.Shell(cmd)))
            {
                int ms = (int)Math.Min(int.MaxValue, timeout * 1000);
                if (!p.WaitForExit(ms))
                    return J.Ok("status", "fehler", "kind", kind, "cmd", cmd, "detail", "Zeitüberschreitung, Installer läuft noch");
                code = p.ExitCode;
            }
            string text = "";
            if (File.Exists(log)) text = Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(log));
            long files = Directory.Exists(target) ? CountFiles(target) : 0;
            string status;
            if (kind == "inno")
            {
                if (text.Contains("Installation process succeeded")) status = "ok";
                else if (text.Contains("Failed to proceed to next wizard page")) status = "eula";
                else status = "fehler";
            }
            else status = code == 0 && files > 0 ? "ok" : "fehler";
            var nz = new List<object>();
            foreach (var l in SplitLines(text)) if (l.Trim().Length > 0) nz.Add(l);
            var tail = nz.Count > 6 ? nz.GetRange(nz.Count - 6, 6) : nz;
            return J.Ok("status", status, "kind", kind, "exit", (long)code, "files", files, "log", log, "cmd", cmd,
                "seconds", (long)(DateTime.UtcNow - start).TotalSeconds, "tail", tail);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct StartupInfo
        {
            public int cb; public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ProcInfo { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, int flags,
            IntPtr env, string cwd, ref StartupInfo si, out ProcInfo pi);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        static string S(J v, string k)
        {
            object o;
            return v.TryGetValue(k, out o) && Py.Truthy(o) ? Py.Repr(o) : "";
        }

        [Op("uninstall")]
        static J Uninstall(Conn c, Req r)
        {
            string name = r.Need("name");
            string q = Lower(name);
            var u = Reg.Read(Common.HklmUninstall);
            var hits = new List<KeyValuePair<string, J>>();
            foreach (var key in (u != null ? u.Subs : new List<string>()))
            {
                var e = Reg.Read(Common.HklmUninstall + "\\" + key, false);
                if (e == null) continue;
                var v = e.Vals;
                string disp = S(v, "DisplayName");
                if (q == Lower(key) || q == Lower(disp))
                {
                    hits = new List<KeyValuePair<string, J>> { new KeyValuePair<string, J>(key, v) };
                    break;
                }
                if (Lower(key).Contains(q) || Lower(disp).Contains(q)) hits.Add(new KeyValuePair<string, J>(key, v));
            }
            var info = new List<object>();
            foreach (var h in hits)
            {
                var v = h.Value;
                string un = S(v, "QuietUninstallString");
                if (un == "") un = S(v, "UninstallString");
                info.Add(J.O("key", h.Key, "name", S(v, "DisplayName"), "version", S(v, "DisplayVersion"),
                    "uninstall", un, "location", S(v, "InstallLocation")));
            }
            if (!r.Bool("run")) return J.Ok("entries", info);
            if (info.Count != 1)
                return J.O("ok", false, "error", "'" + name + "' passt auf " + info.Count + " Einträge, genauer angeben", "entries", info);
            string cmd = (string)((J)info[0])["uninstall"];
            if (cmd == "") return J.O("ok", false, "error", "kein UninstallString", "entries", info);
            var m = Regex.Match(cmd, @"msiexec(\.exe)?\s+/[ix]\s*(\{[0-9a-f-]+\})", RegexOptions.IgnoreCase);
            if (m.Success) cmd = "msiexec.exe /x " + m.Groups[2].Value + " /qb";
            var si = new StartupInfo();
            si.cb = Marshal.SizeOf(typeof(StartupInfo));
            ProcInfo pi;
            if (!CreateProcessW(null, new StringBuilder(cmd), IntPtr.Zero, IntPtr.Zero, false, 0x208, IntPtr.Zero, null, ref si, out pi))
            {
                int err = Marshal.GetLastWin32Error();
                throw new OpError("OSError", "[WinError " + err + "] " + WinMsg(err));
            }
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
            return J.Ok("entries", info, "started", cmd);
        }
    }
}
