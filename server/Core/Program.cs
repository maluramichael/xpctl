using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace XpServer
{
    /// <summary>Start: Argumente, Konfiguration, Passwort, Server, Wächter, Tray.</summary>
    public static class Program
    {
        public const int Version = 17;
        public const int DefaultPort = 5050;
        public const int ExitHandover = 42;   // Update übergeben, Wächter beendet sich

        public static string ExePath = Process.GetCurrentProcess().MainModule.FileName;
        public static string Here = Path.GetDirectoryName(ExePath);
        public static Options Opt;
        public static Server Srv;
        public static byte[] Key;
        public static List<Cidr> Allow;

        public class Options
        {
            public string Bind = "0.0.0.0";
            public int Port = DefaultPort;
            public List<string> Allow = new List<string>();
            public string Config;
            public string KeyFile;
            public bool AfterUpdate;
            public int CrashCount;
            public bool NoWatchdog;
            public bool Demo;           // --demo: neutrale Anzeige für Screenshots (Rechnername, IPs, Pfad, Port)
            public int WatchPid;        // --watchdog PID: dieser Prozess ist nur der Wächter
            public List<string> Passthrough = new List<string>();   // Argumente für Neustarts
        }

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--write-icon") { Ui.IconMaker.WriteIco(args[1]); return 0; }
            Opt = Parse(args);
            if (Opt.WatchPid != 0) return Supervisor.Watch(Opt);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Wie früher die .bat (cd /d): relative Pfade in run/put/get beziehen sich auf den Exe-Ordner,
            // egal ob über Autostart, Doppelklick oder Update gestartet.
            try { Directory.SetCurrentDirectory(Here); } catch { }
            Log.FilePath = Path.Combine(Here, "xpserver.log");
            Log.LoadHistory(400);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Log.Write("ABSTURZ: " + e.ExceptionObject, LogKind.Error);
            Application.ThreadException += (s, e) => Log.Write("UI-Fehler: " + e.Exception, LogKind.Error);

            try
            {
                string cfg = Opt.Config ?? Environment.GetEnvironmentVariable("XPCTL_CONFIG") ?? Path.Combine(Here, Cfg.Name);
                Cfg.Load(cfg);
            }
            catch (Exception e)
            {
                Ui.Dialogs.Error("Konfiguration lässt sich nicht lesen:\n" + e.Message);
                return 1;
            }

            var nets = Opt.Allow.Count > 0 ? Opt.Allow : SplitEnv("XPCTL_ALLOW");
            if (nets.Count == 0) nets = Cfg.Strs("allow");
            if (nets.Count == 0)
            {
                // Ersteinrichtung: Netz, Passwort, Autostart, Firewall in einem Dialog.
                string keyPath = Path.Combine(Here, "xpctl.key");
                bool needKey = Opt.KeyFile == null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("XPCTL_KEY")) && !File.Exists(keyPath);
                string cfgPath = Cfg.PathUsed ?? Path.Combine(Here, Cfg.Name);
                if (!Ui.Setup.Run(cfgPath, keyPath, needKey)) return 1;
                Cfg.Load(cfgPath);
                nets = Cfg.Strs("allow");
            }
            try
            {
                Allow = new List<Cidr>();
                foreach (var n in nets) Allow.Add(new Cidr(n));
            }
            catch (Exception e)
            {
                Ui.Dialogs.Error("Ungültiges Netz in \"allow\": " + e.Message);
                return 1;
            }

            Key = LoadKey();
            if (Key == null) return 1;
            // Passwort nicht an Kindprozesse (run, run --detach) vererben. Wächter und Update reichen es gezielt weiter.
            Environment.SetEnvironmentVariable("XPCTL_KEY", null);

            Srv = new Server(IPAddress.Parse(Opt.Bind), Opt.Port, Key, Allow);
            try { Srv.Start(Opt.AfterUpdate || Opt.CrashCount > 0 ? 15 : 0); }
            catch (Exception e)
            {
                if (!Opt.AfterUpdate && Opt.CrashCount == 0)
                    Ui.Dialogs.Error("Port " + Opt.Port + " lässt sich nicht öffnen:\n" + e.Message +
                        "\n\nLäuft xpserver schon (Symbol unten rechts im Infobereich) oder noch der alte Python-Server (xpserver.bat)? Dann diesen zuerst beenden.");
                else Log.Write("Port " + Opt.Port + " nicht offen: " + e.Message, LogKind.Error);
                return 1;
            }
            var allowText = new List<string>();
            foreach (var n in Allow) allowText.Add(n.ToString());
            Log.Write(string.Format("xpserver v{0} lauscht auf {1}:{2}, erlaubt: {3}", Version, Opt.Bind, Opt.Port,
                string.Join(", ", allowText.ToArray())), LogKind.System);

            Supervisor.SignalReady(Opt.Port);
            string bak = ExePath + ".bak";
            // Neue Version läuft, Rollback nicht mehr nötig. Die alte Exe (.bak) ist gesperrt, bis die alte Instanz weg ist.
            if (File.Exists(bak))
                new Thread(() =>
                {
                    for (int i = 0; i < 60 && File.Exists(bak); i++)
                    {
                        try { File.Delete(bak); } catch { Thread.Sleep(500); }
                    }
                }) { IsBackground = true }.Start();
            if (!Opt.NoWatchdog) Supervisor.StartWatchdog(Opt);

            Application.Run(new Ui.TrayApp());
            Srv.Stop();
            Log.Write("beendet.", LogKind.System);
            return 0;
        }

        static List<string> SplitEnv(string name)
        {
            var r = new List<string>();
            foreach (var p in (Environment.GetEnvironmentVariable(name) ?? "").Split(','))
                if (p.Trim().Length > 0) r.Add(p.Trim());
            return r;
        }

        /// <summary>Passwort: --key-file, XPCTL_KEY, xpctl.key neben der Exe, sonst Dialog (optional merken).</summary>
        static byte[] LoadKey()
        {
            string kf = Opt.KeyFile;
            if (kf != null && !Path.IsPathRooted(kf)) kf = Path.Combine(Here, kf);
            if (kf != null)
            {
                try { return Trim(File.ReadAllBytes(kf)); }
                catch (Exception e) { Ui.Dialogs.Error("Passwortdatei nicht lesbar: " + e.Message); return null; }
            }
            var env = Environment.GetEnvironmentVariable("XPCTL_KEY");
            if (!string.IsNullOrEmpty(env)) return Encoding.UTF8.GetBytes(env.Trim());
            string def = Path.Combine(Here, "xpctl.key");
            if (File.Exists(def))
            {
                var k = Trim(File.ReadAllBytes(def));
                if (k.Length > 0) return k;
            }
            bool remember;
            string pw = Ui.Dialogs.AskPassword(out remember);
            if (string.IsNullOrEmpty(pw)) return null;
            var key = Encoding.UTF8.GetBytes(pw.Trim());
            if (remember)
            {
                try { File.WriteAllBytes(def, key); } catch (Exception e) { Ui.Dialogs.Error("xpctl.key nicht geschrieben: " + e.Message); }
            }
            return key;
        }

        static byte[] Trim(byte[] b)
        {
            int s = 0, e = b.Length;
            while (s < e && (b[s] == ' ' || b[s] == '\r' || b[s] == '\n' || b[s] == '\t')) s++;
            while (e > s && (b[e - 1] == ' ' || b[e - 1] == '\r' || b[e - 1] == '\n' || b[e - 1] == '\t')) e--;
            var r = new byte[e - s];
            Array.Copy(b, s, r, 0, r.Length);
            return r;
        }

        /// <summary>Port, wie er in der Oberfläche erscheint (im Demo-Modus immer der Standardport).</summary>
        public static int ShownPort { get { return Opt.Demo ? DefaultPort : Opt.Port; } }

        static readonly System.Text.RegularExpressions.Regex PrivIp =
            new System.Text.RegularExpressions.Regex(@"\b(?:10|172|192)\.\d{1,3}\.\d{1,3}\.(\d{1,3})\b");

        /// <summary>Text für die Oberfläche. Im Demo-Modus (--demo, für Screenshots in der Doku) werden
        /// Rechnername, private IPs, Programmordner und Port durch neutrale Werte ersetzt.</summary>
        public static string Show(string s)
        {
            if (s == null || Opt == null || !Opt.Demo) return s;
            s = PrivIp.Replace(s, m => "192.168.1." + m.Groups[1].Value);
            s = s.Replace(Here.TrimEnd('\\'), @"C:\xpctl");
            s = System.Text.RegularExpressions.Regex.Replace(s, System.Text.RegularExpressions.Regex.Escape(Environment.MachineName), "retro-pc",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return s.Replace(":" + Opt.Port, ":" + DefaultPort).Replace("Port " + Opt.Port, "Port " + DefaultPort);
        }

        static Options Parse(string[] a)
        {
            var o = new Options();
            for (int i = 0; i < a.Length; i++)
            {
                string x = a[i];
                Func<string> next = () =>
                {
                    if (i + 1 >= a.Length) throw new ArgumentException(x + " braucht einen Wert");
                    return a[++i];
                };
                switch (x)
                {
                    case "--bind": o.Bind = next(); o.Passthrough.Add(x); o.Passthrough.Add(o.Bind); break;
                    case "--port": o.Port = int.Parse(next()); o.Passthrough.Add(x); o.Passthrough.Add(o.Port.ToString()); break;
                    case "--allow": { var v = next(); o.Allow.Add(v); o.Passthrough.Add(x); o.Passthrough.Add(v); break; }
                    case "--config": o.Config = next(); o.Passthrough.Add(x); o.Passthrough.Add(o.Config); break;
                    case "--key-file": o.KeyFile = next(); o.Passthrough.Add(x); o.Passthrough.Add(o.KeyFile); break;
                    case "--no-watchdog": o.NoWatchdog = true; o.Passthrough.Add(x); break;
                    case "--demo": o.Demo = true; o.Passthrough.Add(x); break;
                    case "--after-update": o.AfterUpdate = true; break;
                    case "--crash-count": o.CrashCount = int.Parse(next()); break;
                    case "--watchdog": o.WatchPid = int.Parse(next()); break;
                }
            }
            return o;
        }

        /// <summary>Argumente für einen Neustart, mit Anführungszeichen.</summary>
        public static string Quote(IEnumerable<string> args)
        {
            var sb = new StringBuilder();
            foreach (var x in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                if (x.IndexOf(' ') >= 0 || x.Length == 0) sb.Append('"').Append(x).Append('"');
                else sb.Append(x);
            }
            return sb.ToString();
        }
    }
}
