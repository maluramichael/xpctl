using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace XpServer.Ui
{
    /// <summary>Vollbild-Erkennung, Einstellungen der Oberfläche (HKCU), Autostart.</summary>
    public static class Desk
    {
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);

        /// <summary>Läuft gerade etwas im Vollbild (Spiel, Video)? Dann keine Hinweise einblenden.</summary>
        public static bool FullscreenActive()
        {
            try
            {
                IntPtr h = GetForegroundWindow();
                if (h == IntPtr.Zero) return false;
                var sb = new StringBuilder(64);
                GetClassNameW(h, sb, 64);
                string cls = sb.ToString();
                if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd") return false;
                RECT r;
                if (!GetWindowRect(h, out r)) return false;
                var scr = Screen.FromHandle(h).Bounds;
                return r.L <= scr.Left && r.T <= scr.Top && r.R >= scr.Right && r.B >= scr.Bottom;
            }
            catch { return false; }
        }

        const string UiKey = @"Software\xpctl\xpserver";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool GetFlag(string name, bool def)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(UiKey))
                {
                    var v = k == null ? null : k.GetValue(name);
                    return v == null ? def : Convert.ToInt32(v) != 0;
                }
            }
            catch { return def; }
        }

        public static void SetFlag(string name, bool on)
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(UiKey)) k.SetValue(name, on ? 1 : 0, RegistryValueKind.DWord); }
            catch { }
        }

        /// <summary>Autostart über HKCU\...\Run (nur dieser Benutzer).</summary>
        public static bool Autostart
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    {
                        var v = k == null ? null : k.GetValue("xpserver") as string;
                        return v != null && v.IndexOf(Program.ExePath, StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }
                catch { return false; }
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue("xpserver", "\"" + Program.ExePath + "\"");
                    else if (k.GetValue("xpserver") != null) k.DeleteValue("xpserver");
                }
            }
        }

        /// <summary>Firewall-Ausnahme für die Exe (XP: netsh firewall). Liefert die Ausgabe von netsh.</summary>
        public static string AllowFirewall()
        {
            string cmd = "netsh firewall add allowedprogram program=\"" + Program.ExePath + "\" name=xpserver mode=ENABLE scope=SUBNET";
            return Proc.Sh(cmd, 30).Trim();
        }

        public static void Open(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception e) { Dialogs.Error("Lässt sich nicht öffnen: " + e.Message); }
        }
    }
}
