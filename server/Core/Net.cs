using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace XpServer
{
    /// <summary>Markiert eine Op: static J Name(Conn c, Req r). Wird per Reflection gefunden.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class OpAttribute : Attribute
    {
        public readonly string Name;
        public OpAttribute(string name) { Name = name; }
    }

    /// <summary>Eine Client-Verbindung: Frames mit 4-Byte-Länge (Big Endian) + JSON.</summary>
    public class Conn
    {
        public const int MaxFrame = 16 * 1024 * 1024;
        public const int Chunk = 256 * 1024;
        public readonly Socket Sock;
        public readonly string Ip;

        public Conn(Socket s, string ip) { Sock = s; Ip = ip; }

        public byte[] RecvExact(int n)
        {
            var buf = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = Sock.Receive(buf, got, Math.Min(n - got, 1024 * 1024), SocketFlags.None);
                if (r <= 0) throw new EndOfConnection();
                got += r;
            }
            return buf;
        }

        public void SendFrame(byte[] data) { SendFrame(data, 0, data.Length); }

        public void SendFrame(byte[] data, int off, int len)
        {
            var head = new byte[] { (byte)(len >> 24), (byte)(len >> 16), (byte)(len >> 8), (byte)len };
            Sock.Send(head);
            int sent = 0;
            while (sent < len) sent += Sock.Send(data, off + sent, len - sent, SocketFlags.None);
        }

        public byte[] RecvFrame()
        {
            var h = RecvExact(4);
            int n = (h[0] << 24) | (h[1] << 16) | (h[2] << 8) | h[3];
            if (n < 0 || n > MaxFrame) throw new OpError("Frame zu groß: " + n);
            return RecvExact(n);
        }

        public void SendJson(object o) { SendFrame(Encoding.UTF8.GetBytes(Json.Dump(o))); }

        public J RecvJson()
        {
            var o = Json.Parse(Encoding.UTF8.GetString(RecvFrame())) as J;
            if (o == null) throw new OpError("JSON-Objekt erwartet");
            return o;
        }
    }

    static class NoInherit
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool SetHandleInformation(IntPtr h, int mask, int flags);
        /// <summary>Sockets nicht an Kindprozesse vererben (sonst hält z. B. ein per run gestartetes Programm
        /// oder der Wächter den Port fest). Python 3.4 macht das von selbst (PEP 446), .NET 2.0 nicht.</summary>
        public static void Apply(Socket s) { try { SetHandleInformation(s.Handle, 1, 0); } catch { } }
    }

    public class EndOfConnection : Exception { public EndOfConnection() : base("Verbindung geschlossen") { } }

    /// <summary>Lauscht, prüft Netz und Passwort, verteilt Anfragen an die Ops.</summary>
    public class Server
    {
        public delegate J Handler(Conn c, Req r);
        public static readonly Dictionary<string, Handler> Ops = new Dictionary<string, Handler>();

        public readonly IPAddress Bind;
        public readonly int Port;
        public readonly byte[] Key;
        public readonly List<Cidr> Allow;
        Socket _listen;
        Thread _thread;
        volatile bool _stop;

        /// <summary>Gesperrt: neue Verbindungen werden abgelehnt (Menü "Sperren").</summary>
        public static volatile bool Locked;
        public static int ActiveOps;
        public static int ClientsNow;
        public static long OpsTotal;
        public static long ErrorsTotal;
        public static DateTime Started = DateTime.Now;
        /// <summary>Beginn und Ende jeder Op (für Icon-Animation und Fenster).</summary>
        public static event Action<string, string, bool> OpStarted;   // ip, Beschreibung, true
        public static event Action<string, string, bool> OpFinished;  // ip, op, ok

        public Server(IPAddress bind, int port, byte[] key, List<Cidr> allow)
        {
            Bind = bind; Port = port; Key = key; Allow = allow;
        }

        static Server()
        {
            foreach (var t in typeof(Server).Assembly.GetTypes())
                foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var a = (OpAttribute)Attribute.GetCustomAttribute(m, typeof(OpAttribute));
                    if (a == null) continue;
                    if (Ops.ContainsKey(a.Name)) throw new InvalidOperationException("Op doppelt: " + a.Name);
                    Ops[a.Name] = (Handler)Delegate.CreateDelegate(typeof(Handler), m);
                }
        }

        /// <summary>Öffnet den Port. Nach Update/Neustart (retrySec) wird kurz wiederholt, bis die alte Instanz ihn freigibt.
        /// Kein ExclusiveAddressUse: das würde das Neu-Binden blockieren, solange noch Verbindungen der alten Instanz offen sind.</summary>
        public void Start(int retrySec = 0)
        {
            DateTime until = DateTime.Now.AddSeconds(retrySec);
            while (true)
            {
                try { TryBind(); break; }
                catch (SocketException) { if (DateTime.Now >= until) throw; Thread.Sleep(300); }
            }
            _stop = false;
            _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "accept" };
            _thread.Start();
        }

        void TryBind()
        {
            _listen = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            NoInherit.Apply(_listen);
            try { _listen.Bind(new IPEndPoint(Bind, Port)); }
            catch { _listen.Close(); throw; }
            _listen.Listen(5);
        }

        public void Stop()
        {
            _stop = true;
            try { _listen.Close(); } catch { }
        }

        void AcceptLoop()
        {
            while (!_stop)
            {
                Socket s;
                try { s = _listen.Accept(); }
                catch { if (_stop) return; Thread.Sleep(200); continue; }
                NoInherit.Apply(s);
                var t = new Thread(() => Serve(s)) { IsBackground = true, Name = "client" };
                t.SetApartmentState(ApartmentState.STA);   // Shell-COM (IShellLink) braucht STA
                t.Start();
            }
        }

        bool Allowed(IPAddress ip)
        {
            foreach (var n in Allow) if (n.Contains(ip)) return true;
            return false;
        }

        void Serve(Socket s)
        {
            string ip = "?";
            Interlocked.Increment(ref ClientsNow);
            try
            {
                var ep = (IPEndPoint)s.RemoteEndPoint;
                ip = ep.Address.ToString();
                if (!Allowed(ep.Address))
                {
                    Log.Write("ABGELEHNT " + ip + " (nicht im erlaubten Netz)", LogKind.Denied, ip);
                    return;
                }
                if (Locked)
                {
                    Log.Write("ABGELEHNT " + ip + " (Server gesperrt)", LogKind.Denied, ip);
                    return;
                }
                var c = new Conn(s, ip);
                s.ReceiveTimeout = 10000;
                if (!Handshake(c))
                {
                    Log.Write("ABGELEHNT " + ip + " (falsches Passwort)", LogKind.Denied, ip);
                    return;
                }
                s.ReceiveTimeout = 0;
                while (true)
                {
                    var raw = c.RecvJson();
                    var req = new Req(raw);
                    string op = req.Op;
                    if (op == "bye") break;
                    Handler h;
                    if (op == null || !Ops.TryGetValue(op, out h))
                    {
                        c.SendJson(J.O("ok", false, "error", "unbekannte op: " + (op ?? "None")));
                        continue;
                    }
                    string desc = Describe(raw);
                    Log.Write(desc, LogKind.Op, ip);
                    Interlocked.Increment(ref ActiveOps);
                    Interlocked.Increment(ref OpsTotal);
                    Fire(OpStarted, ip, desc, true);
                    J resp;
                    try { resp = h(c, req); }
                    catch (EndOfConnection) { throw; }
                    catch (SocketException) { throw; }
                    catch (Exception e) { resp = J.Err(ErrText(e)); }
                    finally { Interlocked.Decrement(ref ActiveOps); }
                    bool ok = resp.ContainsKey("ok") && Py.Truthy(resp["ok"]);
                    if (!ok)
                    {
                        Interlocked.Increment(ref ErrorsTotal);
                        Log.Write("  Fehler: " + Py.Repr(resp.ContainsKey("error") ? resp["error"] : null), LogKind.Error, ip);
                    }
                    else if (op == "run" && resp.ContainsKey("exit"))
                        Log.Write("  exit=" + Py.Repr(resp["exit"]) + (Py.Truthy(resp["timed_out"]) ? " (TIMEOUT)" : ""), LogKind.Ok, ip);
                    Fire(OpFinished, ip, op, ok);
                    c.SendJson(resp);
                }
            }
            catch (EndOfConnection) { }
            catch (Exception e)
            {
                if (!(e is SocketException se && (se.ErrorCode == 10054 || se.ErrorCode == 10053)))
                    Log.Write("Verbindungsfehler " + ip + ": " + ErrText(e), LogKind.Error, ip);
            }
            finally
            {
                Interlocked.Decrement(ref ClientsNow);
                try { s.Close(); } catch { }
            }
        }

        static void Fire(Action<string, string, bool> ev, string a, string b, bool c)
        {
            if (ev != null) try { ev(a, b, c); } catch { }
        }

        /// <summary>Fehlertext wie Python: "Typ: Meldung".</summary>
        public static string ErrText(Exception e)
        {
            if (e is OpError oe) return oe.PyType + ": " + oe.Message;
            if (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            // COM/Win32-Fehler (HRESULT 0x8007xxxx) wie Python: "OSError: [WinError -2147024893] Meldung"
            int idx = e.Message.IndexOf(" (Exception from HRESULT:", StringComparison.Ordinal);
            if (idx >= 0)
                return "OSError: [WinError " + System.Runtime.InteropServices.Marshal.GetHRForException(e) + "] " + e.Message.Substring(0, idx).TrimEnd('.');
            string t = e.GetType().Name;
            if (e is System.IO.FileNotFoundException || e is System.IO.DirectoryNotFoundException) t = "FileNotFoundError";
            else if (e is UnauthorizedAccessException) t = "PermissionError";
            else if (e is System.IO.IOException) t = "OSError";
            else if (e is KeyNotFoundException) t = "KeyError";
            else if (e is FormatException || e is ArgumentException) t = "ValueError";
            return t + ": " + e.Message;
        }

        // ---------- Anmeldung (HMAC-SHA256, Challenge in beide Richtungen) ----------

        string Mac(string role, string nonce)
        {
            using (var h = new HMACSHA256(Key))
            {
                var d = h.ComputeHash(Encoding.ASCII.GetBytes(role + "|" + nonce));
                return Hex(d);
            }
        }

        public static string Hex(byte[] d)
        {
            var sb = new StringBuilder(d.Length * 2);
            foreach (var b in d) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        bool Handshake(Conn c)
        {
            var rnd = new byte[16];
            new RNGCryptoServiceProvider().GetBytes(rnd);
            string nonce = Hex(rnd);
            c.SendJson(J.O("hello", "xpserver", "version", (long)Program.Version, "nonce", nonce));
            var resp = c.RecvJson();
            string got = resp.ContainsKey("auth") ? Py.Repr(resp["auth"]) : "";
            if (!FixedEq(got, Mac("client", nonce)))
            {
                Thread.Sleep(1000);
                c.SendJson(J.O("ok", false, "error", "auth"));
                return false;
            }
            string cnonce = resp.ContainsKey("cnonce") ? Py.Repr(resp["cnonce"]) : "";
            c.SendJson(J.O("ok", true, "auth", Mac("server", cnonce)));
            return true;
        }

        static bool FixedEq(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int d = 0;
            for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }

        /// <summary>describe(req): Logzeile einer Anfrage.</summary>
        public static string Describe(J req)
        {
            string op = req.ContainsKey("op") ? Py.Repr(req["op"]) : "None";
            if (op == "run")
                return "run" + (Py.Truthy(req.ContainsKey("detach") ? req["detach"] : null) ? " [detach]" : "") + ": " + Py.Repr(req.ContainsKey("cmd") ? req["cmd"] : null);
            if (op == "put" || op == "get")
                return op + ": " + Py.Repr(req.ContainsKey("path") ? req["path"] : null);
            var keys = new List<string>(req.Keys);
            keys.Sort(StringComparer.Ordinal);
            var parts = new List<string>();
            foreach (var k in keys) if (k != "op") parts.Add(k + "=" + Py.Repr(req[k]));
            return parts.Count > 0 ? op + " " + string.Join(", ", parts.ToArray()) : op;
        }
    }

    /// <summary>IPv4-Netz in CIDR-Schreibweise.</summary>
    public class Cidr
    {
        readonly uint _net, _mask;
        readonly string _text;

        public Cidr(string s)
        {
            s = s.Trim();
            int bits = 32;
            string addr = s;
            int i = s.IndexOf('/');
            if (i >= 0) { addr = s.Substring(0, i); bits = int.Parse(s.Substring(i + 1), CultureInfo.InvariantCulture); }
            if (bits < 0 || bits > 32) throw new FormatException("ungültiges Netz: " + s);
            _mask = bits == 0 ? 0 : 0xFFFFFFFFu << (32 - bits);
            _net = ToUInt(IPAddress.Parse(addr)) & _mask;
            _text = FromUInt(_net) + "/" + bits;
        }

        static uint ToUInt(IPAddress a)
        {
            var b = a.GetAddressBytes();
            if (b.Length != 4) return 0;
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        static string FromUInt(uint v)
        {
            return string.Format("{0}.{1}.{2}.{3}", v >> 24, (v >> 16) & 255, (v >> 8) & 255, v & 255);
        }

        public bool Contains(IPAddress a)
        {
            if (a.AddressFamily != AddressFamily.InterNetwork) return false;
            return (ToUInt(a) & _mask) == _net;
        }

        public override string ToString() { return _text; }
    }
}
