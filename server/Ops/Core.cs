using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;

namespace XpServer
{
    /// <summary>Grund-Ops: ping, run, put, get, update.</summary>
    public static class CoreOps
    {
        static string SelfSha256()
        {
            using (var f = File.OpenRead(Program.ExePath))
            using (var h = SHA256.Create()) return Server.Hex(h.ComputeHash(f));
        }

        /// <summary>platform.platform() auf XP: "Windows-XP-5.1.2600-SP3".</summary>
        static string Platform()
        {
            var v = Environment.OSVersion;
            string name = v.Version.Major == 5 && v.Version.Minor == 1 ? "XP" :
                          v.Version.Major == 5 && v.Version.Minor == 2 ? "2003Server" : v.Version.Major + "." + v.Version.Minor;
            string sp = v.ServicePack.Replace("Service Pack ", "SP");
            return string.Format("Windows-{0}-{1}.{2}.{3}{4}", name, v.Version.Major, v.Version.Minor, v.Version.Build,
                sp.Length > 0 ? "-" + sp : "");
        }

        [Op("ping")]
        static J Ping(Conn c, Req r)
        {
            return J.Ok(
                "version", (long)Program.Version,
                "sha256", SelfSha256(),
                "supervised", true,
                "script", Program.ExePath,
                "host", Dns.GetHostName(),
                "os", Platform(),
                "runtime", ".NET " + Environment.Version,
                "kind", "net",
                "time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                "cwd", Environment.CurrentDirectory);
        }

        [Op("run")]
        static J Run(Conn c, Req r)
        {
            string cmd = r.Need("cmd");
            string cwd = r.Or("cwd", null);
            double timeout = r.Dbl("timeout", 60);
            if (r.Bool("detach"))
                return J.Ok("detached", true, "pid", (long)Proc.Detach(cmd, cwd));
            var res = Proc.Run(Proc.Shell(cmd, cwd), timeout);
            return J.Ok("exit", (long)res.Exit, "timed_out", res.TimedOut,
                "stdout", Proc.Oem.GetString(res.Out), "stderr", Proc.Oem.GetString(res.Err));
        }

        [Op("put")]
        static J Put(Conn c, Req r)
        {
            string path = r.Need("path");
            long size = r.Int("size");
            string name = r.Or("name", "datei");
            if (path.EndsWith("\\") || path.EndsWith("/") || Directory.Exists(path)) path = Path.Combine(path, name);
            string parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                if (r.Bool("mkdir")) Directory.CreateDirectory(parent);
                else return J.Err("Zielordner fehlt: " + parent);
            }
            c.SendJson(J.Ok("ready", true, "target", path));
            string part = path + ".part";
            long got = 0;
            using (var h = SHA256.Create())
            {
                using (var f = new FileStream(part, FileMode.Create, FileAccess.Write))
                {
                    while (got < size)
                    {
                        var data = c.RecvFrame();
                        f.Write(data, 0, data.Length);
                        h.TransformBlock(data, 0, data.Length, null, 0);
                        got += data.Length;
                    }
                }
                h.TransformFinalBlock(new byte[0], 0, 0);
                if (File.Exists(path)) File.Delete(path);
                File.Move(part, path);
                return J.Ok("size", got, "sha256", Server.Hex(h.Hash), "target", path);
            }
        }

        [Op("get")]
        static J Get(Conn c, Req r)
        {
            string path = r.Need("path");
            if (!File.Exists(path)) return J.Err("keine Datei: " + path);
            long size = new FileInfo(path).Length;
            c.SendJson(J.Ok("ready", true, "size", size));
            using (var h = SHA256.Create())
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var buf = new byte[Conn.Chunk];
                int n;
                while ((n = f.Read(buf, 0, buf.Length)) > 0)
                {
                    h.TransformBlock(buf, 0, n, null, 0);
                    c.SendFrame(buf, 0, n);
                }
                h.TransformFinalBlock(new byte[0], 0, 0);
                return J.Ok("size", size, "sha256", Server.Hex(h.Hash));
            }
        }

        /// <summary>Ersetzt xpserver.exe durch die gesendete Version und übergibt an sie.
        /// Die alte bleibt als xpserver.exe.bak, bis die neue lauscht; kommt sie nicht hoch, läuft die alte weiter.</summary>
        [Op("update")]
        static J Update(Conn c, Req r)
        {
            long size = r.Int("size");
            string want = r.Need("sha256");
            c.SendJson(J.Ok("ready", true));
            var ms = new MemoryStream();
            while (ms.Length < size) { var d = c.RecvFrame(); ms.Write(d, 0, d.Length); }
            var data = ms.ToArray();
            string got;
            using (var h = SHA256.Create()) got = Server.Hex(h.ComputeHash(data));
            if (got != want) return J.Err("Prüfsumme stimmt nicht");
            if (data.Length < 2 || data[0] != 'M' || data[1] != 'Z')
                return J.Err("keine Exe: xpctl update schickt für diesen Server xpserver.exe (server/dist), nicht xpserver.py");
            Supervisor.Install(data);
            Log.Write("  Update eingespielt (" + data.Length + " Bytes), starte neu", LogKind.System);
            Supervisor.Handover();
            return J.Ok("restarting", true, "sha256", want);
        }
    }
}
