using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace XpServer
{
    /// <summary>Ersetzt xpserver.bat: Neustart nach Absturz (max. 3 am Stück), Update mit Rollback.</summary>
    public static class Supervisor
    {
        static EventWaitHandle ReadyEvent(int port)
        {
            return new EventWaitHandle(false, EventResetMode.ManualReset, "xpserver-ready-" + port);
        }

        /// <summary>Neue Instanz meldet: Port offen, alles bereit.</summary>
        public static void SignalReady(int port)
        {
            try { using (var e = ReadyEvent(port)) e.Set(); } catch { }
        }

        static ProcessStartInfo Self(List<string> args)
        {
            var psi = new ProcessStartInfo(Program.ExePath, Program.Quote(args))
            {
                UseShellExecute = false,
                WorkingDirectory = Program.Here,
            };
            // Passwort über die Umgebung weitergeben (es kann aus dem Dialog stammen).
            psi.EnvironmentVariables["XPCTL_KEY"] = Encoding.UTF8.GetString(Program.Key);
            return psi;
        }

        /// <summary>Startet den Wächter: zweiter xpserver.exe-Prozess ohne Fenster, der auf uns wartet.</summary>
        public static void StartWatchdog(Program.Options o)
        {
            try
            {
                var a = new List<string> { "--watchdog", Process.GetCurrentProcess().Id.ToString(), "--crash-count", o.CrashCount.ToString() };
                a.AddRange(o.Passthrough);
                Process.Start(Self(a)).Dispose();
            }
            catch (Exception e) { Log.Write("Wächter nicht gestartet: " + e.Message, LogKind.Error); }
        }

        /// <summary>Wächter-Modus: wartet auf den Server-Prozess, startet ihn nach einem Absturz neu.</summary>
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern int WaitForSingleObject(IntPtr h, int ms);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool GetExitCodeProcess(IntPtr h, out int code);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        public static int Watch(Program.Options o)
        {
            // Eigenes Handle (SYNCHRONIZE | PROCESS_QUERY_INFORMATION), sonst ist der Exit-Code nach dem Ende nicht mehr lesbar.
            IntPtr h = OpenProcess(0x00100000 | 0x0400, false, o.WatchPid);
            if (h == IntPtr.Zero) return 0;
            DateTime start = DateTime.Now;
            try { start = Process.GetProcessById(o.WatchPid).StartTime; } catch { }
            WaitForSingleObject(h, -1);
            int code;
            if (!GetExitCodeProcess(h, out code)) code = -1;
            CloseHandle(h);
            if (code == 0 || code == Program.ExitHandover) return 0;

            string here = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            string logf = Path.Combine(here, "xpserver.log");
            Action<string> log = m =>
            {
                try { File.AppendAllText(logf, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + m + "\r\n", new UTF8Encoding(false)); } catch { }
            };
            int count = (DateTime.Now - start).TotalSeconds > 60 ? 1 : o.CrashCount + 1;
            log(string.Format("Absturz, Exit-Code {0} (Nr. {1})", code, count));
            string bak = exe + ".bak";
            if (File.Exists(bak))
            {
                log("Rollback auf xpserver.exe.bak");
                try { File.Copy(bak, exe, true); File.Delete(bak); } catch (Exception e) { log("Rollback fehlgeschlagen: " + e.Message); }
            }
            if (count > 3) { log("3 Abstürze am Stück, kein Neustart mehr."); return 1; }
            Thread.Sleep(2000);
            var a = new List<string>(o.Passthrough) { "--crash-count", count.ToString() };
            var psi = new ProcessStartInfo(exe, Program.Quote(a)) { UseShellExecute = false, WorkingDirectory = here };
            try { Process.Start(psi).Dispose(); } catch (Exception e) { log("Neustart fehlgeschlagen: " + e.Message); }
            return 0;
        }

        /// <summary>Prüft die neue Exe, tauscht sie ein (alte als .bak) und übergibt an die neue Version.
        /// Kommt die neue nicht hoch, läuft die alte weiter und die Dateien werden zurückgetauscht.</summary>
        public static string Install(byte[] data)
        {
            string exe = Program.ExePath, neu = exe + ".new", bak = exe + ".bak";
            File.WriteAllBytes(neu, data);
            try
            {
                var an = AssemblyName.GetAssemblyName(neu);
                if (!string.Equals(an.Name, "xpserver", StringComparison.OrdinalIgnoreCase))
                    throw new OpError("keine xpserver.exe (Assembly heißt " + an.Name + ")");
            }
            catch (BadImageFormatException)
            {
                File.Delete(neu);
                throw new OpError("keine gültige .NET-Exe");
            }
            catch { File.Delete(neu); throw; }
            if (File.Exists(bak)) File.Delete(bak);
            File.Move(exe, bak);      // laufende Exe darf umbenannt werden
            File.Move(neu, exe);
            return bak;
        }

        /// <summary>Neustart über das Menü: gleiche Exe, gleiche Argumente, Übergabe wie beim Update.</summary>
        public static void Restart()
        {
            Log.Write("Neustart über das Symbol", LogKind.System);
            Handover();
        }

        public static void Handover()
        {
            var t = new Thread(() =>
            {
                Thread.Sleep(500);
                string exe = Program.ExePath, bak = exe + ".bak";
                using (var ready = ReadyEvent(Program.Opt.Port))
                {
                    ready.Reset();
                    Program.Srv.Stop();
                    Process child = null;
                    try
                    {
                        var a = new List<string>(Program.Opt.Passthrough) { "--after-update" };
                        child = Process.Start(Self(a));
                        int which = WaitHandle.WaitAny(new WaitHandle[] { ready, new ProcessWaitHandle(child) }, 30000, false);
                        if (which == 0)
                        {
                            Log.Write("  neue Version läuft, alte beendet sich", LogKind.System);
                            Ui.TrayApp.Quit(Program.ExitHandover);
                            return;
                        }
                        Log.Write("  neue Version kam nicht hoch, Rollback", LogKind.Error);
                    }
                    catch (Exception e) { Log.Write("  Update-Start fehlgeschlagen: " + e.Message + ", Rollback", LogKind.Error); }
                    try { if (child != null && !child.HasExited) child.Kill(); } catch { }
                    try
                    {
                        if (File.Exists(bak))
                        {
                            if (File.Exists(exe)) File.Move(exe, exe + ".failed-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
                            File.Move(bak, exe);
                        }
                    }
                    catch (Exception e) { Log.Write("  Rollback der Dateien fehlgeschlagen: " + e.Message, LogKind.Error); }
                    try { Program.Srv.Start(10); Log.Write("  alte Version lauscht wieder", LogKind.System); }
                    catch (Exception e) { Log.Write("  Port nicht wieder offen: " + e.Message, LogKind.Error); }
                }
            }) { IsBackground = true, Name = "update" };
            t.Start();
        }

        class ProcessWaitHandle : WaitHandle
        {
            public ProcessWaitHandle(Process p)
            {
                SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(p.Handle, false);
            }
        }
    }
}
