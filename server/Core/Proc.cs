using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace XpServer
{
    /// <summary>Kindprozesse ohne Konsolenfenster (statt Pythons subprocess mit shell=True).</summary>
    public static class Proc
    {
        [DllImport("kernel32.dll")] static extern int GetOEMCP();

        static Encoding _oem;
        /// <summary>oem_encoding: Codepage der Konsole (cp850 auf deutschem XP).</summary>
        public static Encoding Oem
        {
            get
            {
                if (_oem == null)
                {
                    try { _oem = Encoding.GetEncoding(GetOEMCP()); } catch { _oem = Encoding.UTF8; }
                }
                return _oem;
            }
        }

        public static string ComSpec
        {
            get { return Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"; }
        }

        /// <summary>Wie Popen(cmd, shell=True): cmd.exe /c "cmd" (äußere Anführungszeichen wie Python).</summary>
        public static ProcessStartInfo Shell(string cmd, string cwd = null)
        {
            var psi = new ProcessStartInfo(ComSpec, "/c \"" + cmd + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (!string.IsNullOrEmpty(cwd)) psi.WorkingDirectory = cwd;
            return psi;
        }

        public class Result
        {
            public int Exit;
            public bool TimedOut;
            public byte[] Out = new byte[0];
            public byte[] Err = new byte[0];
            public int Pid;
        }

        /// <summary>Startet, liest stdout/stderr parallel, beendet bei Timeout den ganzen Baum (taskkill /T).
        /// mergeErr: stderr landet in Out (wie stderr=STDOUT).</summary>
        public static Result Run(ProcessStartInfo psi, double timeoutSec, bool mergeErr = false)
        {
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            var r = new Result();
            using (var p = Process.Start(psi))
            {
                r.Pid = p.Id;
                try { p.StandardInput.Close(); } catch { }
                var outMs = new MemoryStream();
                var errMs = mergeErr ? outMs : new MemoryStream();
                var t1 = Pump(p.StandardOutput.BaseStream, outMs);
                var t2 = Pump(p.StandardError.BaseStream, errMs);
                int ms = timeoutSec <= 0 ? int.MaxValue : (int)Math.Min(int.MaxValue, timeoutSec * 1000);
                if (!p.WaitForExit(ms))
                {
                    r.TimedOut = true;
                    KillTree(p.Id);
                    p.WaitForExit(10000);
                }
                t1.Join(5000);
                t2.Join(5000);
                lock (outMs) r.Out = outMs.ToArray();
                if (!mergeErr) lock (errMs) r.Err = errMs.ToArray();
                try { r.Exit = p.ExitCode; } catch { r.Exit = -1; }
            }
            return r;
        }

        static Thread Pump(Stream src, MemoryStream dst)
        {
            var t = new Thread(() =>
            {
                var buf = new byte[8192];
                try
                {
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0) lock (dst) dst.Write(buf, 0, n);
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
            return t;
        }

        /// <summary>sh(cmd, timeout): Ausgabe (stdout+stderr) als Text, bei Fehler "".</summary>
        public static string Sh(string cmd, double timeoutSec = 20)
        {
            try
            {
                var r = Run(Shell(cmd), timeoutSec, true);
                return Oem.GetString(r.Out);
            }
            catch { return ""; }
        }

        /// <summary>kill_tree: taskkill /F /T /PID.</summary>
        public static void KillTree(int pid)
        {
            try
            {
                var psi = new ProcessStartInfo("taskkill", "/F /T /PID " + pid)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit(15000);
                }
            }
            catch { }
        }

        /// <summary>Losgelöst starten (run --detach): Kind bekommt kein Konsolenfenster, wir warten nicht.</summary>
        public static int Detach(string cmd, string cwd = null)
        {
            var psi = Shell(cmd, cwd);
            psi.RedirectStandardInput = false;
            psi.RedirectStandardOutput = false;
            psi.RedirectStandardError = false;
            using (var p = Process.Start(psi)) return p.Id;
        }
    }
}
