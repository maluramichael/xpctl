using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace XpServer
{
    /// <summary>Helfer, die mehrere Op-Gruppen brauchen (Pfade, Laufwerke, Programme-Liste).</summary>
    public static class Common
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHGetFolderPathW(IntPtr hwnd, int csidl, IntPtr token, int flags, StringBuilder path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetDriveTypeW(string root);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetVolumeInformationW(string root, StringBuilder name, int nameLen, IntPtr serial,
            IntPtr maxComp, IntPtr flags, StringBuilder fs, int fsLen);

        /// <summary>sysroot: %SystemRoot%.</summary>
        public static string SysRoot()
        {
            var r = Environment.GetEnvironmentVariable("SystemRoot") ?? Environment.GetEnvironmentVariable("windir");
            if (string.IsNullOrEmpty(r)) throw new OpError("Windows-Ordner unbekannt (SystemRoot fehlt)");
            return r;
        }

        /// <summary>folder_path(csidl) über SHGetFolderPathW.</summary>
        public static string FolderPath(int csidl)
        {
            var sb = new StringBuilder(260);
            SHGetFolderPathW(IntPtr.Zero, csidl, IntPtr.Zero, 0, sb);
            return sb.ToString();
        }

        /// <summary>desk_dirs: (All-Users-Desktop, Desktop des Server-Benutzers).</summary>
        public static string AllUsersDesktop { get { return FolderPath(0x19); } }
        public static string MyDesktop { get { return FolderPath(0x10); } }

        /// <summary>launcher_name: Dateiname des konfigurierten Launcher-Skripts, klein.</summary>
        public static string LauncherName()
        {
            var s = Cfg.Str("launcher_script");
            return string.IsNullOrEmpty(s) ? "" : Path.GetFileName(s).ToLowerInvariant();
        }

        /// <summary>cd_drive: Laufwerk der Anfrage, sonst cd_drive aus der Konfiguration, sonst erstes CD-Laufwerk.</summary>
        public static string CdDrive(Req req)
        {
            string d = req.Or("drive", null) ?? (Py.Truthy(Cfg.Get("cd_drive")) ? Cfg.Str("cd_drive") : null);
            if (d != null) return d.TrimEnd('\\');
            for (int i = 0; i < 26; i++)
            {
                string root = (char)('A' + i) + ":\\";
                if (GetDriveTypeW(root) == 5) return root.Substring(0, 2);
            }
            throw new OpError("kein CD/DVD-Laufwerk gefunden (Konfiguration cd_drive oder --drive)");
        }

        /// <summary>vol_label: Volume-Label oder null.</summary>
        public static string VolLabel(string drive)
        {
            var name = new StringBuilder(64);
            var fs = new StringBuilder(64);
            bool ok = GetVolumeInformationW(drive.TrimEnd('\\') + "\\", name, 64, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, fs, 64);
            return ok ? name.ToString() : null;
        }

        // ---------- Programme (Uninstall-Schlüssel) ----------

        public const string HklmUninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        public static readonly Regex KbRe = new Regex(@"\b(KB\d{5,7}|Q\d{6,7})\b", RegexOptions.IgnoreCase);
        public static readonly Regex UpdateNameRe = new Regex(
            @"^(security update|update for|hotfix for|critical update|update rollup|" +
            @"windows xp (hotfix|service pack)|service pack)", RegexOptions.IgnoreCase);

        /// <summary>norm_date: YYYYMMDD oder M/D/YYYY -> YYYY-MM-DD.</summary>
        public static string NormDate(object o)
        {
            string s = (o == null ? "" : Py.Repr(o)).Trim();
            var m = Regex.Match(s, @"^(\d{4})(\d{2})(\d{2})$");
            if (m.Success) return m.Groups[1].Value + "-" + m.Groups[2].Value + "-" + m.Groups[3].Value;
            m = Regex.Match(s, @"^(\d{1,2})/(\d{1,2})/(\d{4})$");
            if (m.Success) return string.Format("{0}-{1:00}-{2:00}", m.Groups[3].Value, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
            m = Regex.Match(s, @"^(\d{4})-(\d{1,2})-(\d{1,2})$");
            if (m.Success) return string.Format("{0}-{1:00}-{2:00}", m.Groups[1].Value, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            return s;
        }

        static string S(J v, string k)
        {
            object o;
            return v.TryGetValue(k, out o) && Py.Truthy(o) ? Py.Repr(o) : "";
        }

        /// <summary>program_kind: "update", "system" oder "".</summary>
        public static string ProgramKind(string name, J v)
        {
            string disp = S(v, "DisplayName");
            if (S(v, "ParentKeyName") != "" || S(v, "ReleaseType") != "") return "update";
            if (Regex.IsMatch(name, @"^(KB|Q)\d{5,7}", RegexOptions.IgnoreCase) || name.ToLowerInvariant().Contains("hotfix")) return "update";
            if (UpdateNameRe.IsMatch(disp) || (KbRe.IsMatch(disp) && Regex.IsMatch(disp, @"\b(update|hotfix|patch)\b", RegexOptions.IgnoreCase)))
                return "update";
            object sc;
            if (v.TryGetValue("SystemComponent", out sc) && sc is long l && l == 1) return "system";
            return "";
        }

        /// <summary>collect_programs: Uninstall-Einträge mit Anzeigenamen, sortiert nach Name.</summary>
        public static List<J> CollectPrograms()
        {
            var outL = new List<J>();
            var u = Reg.Read(HklmUninstall);
            foreach (var name in (u != null ? u.Subs : new List<string>()))
            {
                var e = Reg.Read(HklmUninstall + "\\" + name, false);
                if (e == null) continue;
                var v = e.Vals;
                string disp = S(v, "DisplayName").Trim();
                if (disp == "") continue;
                string date = NormDate(v.ContainsKey("InstallDate") ? v["InstallDate"] : null);
                outL.Add(J.O("name", disp, "version", S(v, "DisplayVersion"), "publisher", S(v, "Publisher"),
                    "date", date != "" ? date : e.Stamp, "kind", ProgramKind(name, v), "key", name,
                    "location", S(v, "InstallLocation")));
            }
            return Py.SortBy(outL, a => ((string)a["name"]).ToLowerInvariant());
        }
    }
}
