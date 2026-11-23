using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace XpServer
{
    /// <summary>Ein Registry-Wert so, wie ihn Pythons winreg liefert.</summary>
    public class RegValue
    {
        public string Name;
        public int Type;      // REG_SZ = 1 ...
        public object Data;   // string, long, List<object> (MULTI_SZ), byte[] (binär) oder null
    }

    /// <summary>Registry über advapi32, verhält sich wie Pythons winreg (Typen, Zeitstempel, kein Expandieren).</summary>
    public static class Reg
    {
        public static readonly UIntPtr HKCR = new UIntPtr(0x80000000u);
        public static readonly UIntPtr HKCU = new UIntPtr(0x80000001u);
        public static readonly UIntPtr HKLM = new UIntPtr(0x80000002u);
        public static readonly UIntPtr HKU = new UIntPtr(0x80000003u);
        public static readonly UIntPtr HKCC = new UIntPtr(0x80000005u);
        const int KEY_READ = 0x20019;

        public static readonly Dictionary<int, string> TypeNames = new Dictionary<int, string> {
            {0, "REG_NONE"}, {1, "REG_SZ"}, {2, "REG_EXPAND_SZ"}, {3, "REG_BINARY"}, {4, "REG_DWORD"},
            {5, "REG_DWORD_BE"}, {6, "REG_LINK"}, {7, "REG_MULTI_SZ"}, {8, "REG_RESOURCE_LIST"}, {11, "REG_QWORD"} };

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegOpenKeyExW(UIntPtr hKey, string sub, int opts, int sam, out UIntPtr res);
        [DllImport("advapi32.dll")]
        static extern int RegCloseKey(UIntPtr hKey);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegQueryInfoKeyW(UIntPtr hKey, IntPtr cls, IntPtr clsLen, IntPtr res, out int nSubs,
            out int maxSubLen, out int maxClsLen, out int nVals, out int maxValNameLen, out int maxValLen,
            IntPtr secDesc, out long lastWrite);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegEnumKeyExW(UIntPtr hKey, int idx, StringBuilder name, ref int nameLen,
            IntPtr res, IntPtr cls, IntPtr clsLen, IntPtr lastWrite);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegEnumValueW(UIntPtr hKey, int idx, StringBuilder name, ref int nameLen,
            IntPtr res, out int type, byte[] data, ref int dataLen);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegQueryValueExW(UIntPtr hKey, string name, IntPtr res, out int type, byte[] data, ref int dataLen);

        /// <summary>hive_handle: HKLM/HKCU/HKU/HKCR/HKCC (auch Langform).</summary>
        public static UIntPtr Hive(string name)
        {
            switch ((name ?? "").ToUpperInvariant())
            {
                case "HKLM": case "HKEY_LOCAL_MACHINE": return HKLM;
                case "HKCU": case "HKEY_CURRENT_USER": return HKCU;
                case "HKU": case "HKEY_USERS": return HKU;
                case "HKCR": case "HKEY_CLASSES_ROOT": return HKCR;
                case "HKCC": case "HKEY_CURRENT_CONFIG": return HKCC;
            }
            throw new OpError("unbekannter Hive: " + name + " (HKLM, HKCU, HKU, HKCR, HKCC)");
        }

        /// <summary>Offener Schlüssel; Dispose schließt ihn.</summary>
        public class Key : IDisposable
        {
            public UIntPtr H;
            public void Dispose() { if (H != UIntPtr.Zero) { RegCloseKey(H); H = UIntPtr.Zero; } }

            public bool Info(out int nSubs, out int nVals, out long lastWrite)
            {
                int a, b, c, d;
                return RegQueryInfoKeyW(H, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out nSubs, out a, out b,
                    out nVals, out c, out d, IntPtr.Zero, out lastWrite) == 0;
            }

            public string EnumKey(int i)
            {
                var sb = new StringBuilder(512);
                int len = sb.Capacity;
                int rc = RegEnumKeyExW(H, i, sb, ref len, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                return rc == 0 ? sb.ToString() : null;
            }

            public RegValue EnumValue(int i)
            {
                int nameCap = 16384, dataLen = 0, type;
                var sb = new StringBuilder(nameCap);
                int nameLen = nameCap;
                int rc = RegEnumValueW(H, i, sb, ref nameLen, IntPtr.Zero, out type, null, ref dataLen);
                if (rc != 0 && rc != 234) return null;
                var data = new byte[Math.Max(dataLen, 0) + 2];
                dataLen = data.Length;
                nameLen = nameCap;
                rc = RegEnumValueW(H, i, sb, ref nameLen, IntPtr.Zero, out type, data, ref dataLen);
                if (rc != 0) return null;
                return new RegValue { Name = sb.ToString(), Type = type, Data = Convert(type, data, dataLen) };
            }

            /// <summary>QueryValueEx; null, wenn es den Wert nicht gibt. name "" = (Standard).</summary>
            public RegValue Query(string name)
            {
                int type, len = 0;
                int rc = RegQueryValueExW(H, name, IntPtr.Zero, out type, null, ref len);
                if (rc != 0 && rc != 234) return null;
                var data = new byte[len + 2];
                len = data.Length;
                rc = RegQueryValueExW(H, name, IntPtr.Zero, out type, data, ref len);
                if (rc != 0) return null;
                return new RegValue { Name = name, Type = type, Data = Convert(type, data, len) };
            }

            public List<string> SubKeys()
            {
                var r = new List<string>();
                int n, v; long t;
                if (!Info(out n, out v, out t)) return r;
                for (int i = 0; i < n; i++) { var s = EnumKey(i); if (s == null) break; r.Add(s); }
                return r;
            }

            public List<RegValue> Values()
            {
                var r = new List<RegValue>();
                int n, v; long t;
                if (!Info(out n, out v, out t)) return r;
                for (int i = 0; i < v; i++) { var x = EnumValue(i); if (x == null) break; r.Add(x); }
                return r;
            }
        }

        public static Key Open(UIntPtr hive, string path)
        {
            UIntPtr h;
            if (RegOpenKeyExW(hive, path ?? "", 0, KEY_READ, out h) != 0) return null;
            return new Key { H = h };
        }

        public static Key Open(string hive, string path) { return Open(Hive(hive), path); }

        /// <summary>Wie winreg: SZ/EXPAND_SZ bis zur ersten NUL, MULTI_SZ als Liste, DWORD/QWORD als Zahl, sonst Bytes.</summary>
        static object Convert(int type, byte[] data, int len)
        {
            switch (type)
            {
                case 1: case 2:
                    {
                        // Wie winreg in Python 3.4: nur eine abschließende NUL entfernen, der Rest bleibt (auch Müll hinter einer NUL).
                        int n = len / 2;
                        if (n > 0 && data[2 * n - 2] == 0 && data[2 * n - 1] == 0) n--;
                        return Encoding.Unicode.GetString(data, 0, 2 * n);
                    }
                case 7:
                    {
                        string s = Encoding.Unicode.GetString(data, 0, len - (len % 2));
                        var l = new List<object>();
                        foreach (var p in s.Split('\0')) if (p.Length > 0) l.Add(p);
                        return l;
                    }
                case 4:
                    return len >= 4 ? (object)(long)BitConverter.ToUInt32(data, 0) : 0L;
                case 11:
                    return len >= 8 ? (object)(long)BitConverter.ToUInt64(data, 0) : 0L;
                default:
                    {
                        if (len == 0 && type == 0) return null;
                        var b = new byte[len];
                        Array.Copy(data, b, len);
                        return b;
                    }
            }
        }

        /// <summary>_plain: str/int bleiben, Listen werden mit Leerzeichen verbunden, Binärdaten fallen weg (null).</summary>
        public static object Plain(object v)
        {
            if (v is string || v is long) return v;
            if (v is List<object> l)
            {
                var parts = new string[l.Count];
                for (int i = 0; i < l.Count; i++) parts[i] = Py.Repr(l[i]);
                return string.Join(" ", parts);
            }
            return null;
        }

        /// <summary>Ergebnis von reg_read: Werte (nur str/int), Unterschlüssel, Datum der letzten Änderung.</summary>
        public class ReadResult
        {
            public J Vals = new J();
            public List<string> Subs = new List<string>();
            public string Stamp = "";
        }

        /// <summary>reg_read(path, want_subs, hive): null, wenn der Schlüssel fehlt. hive Standard HKLM.</summary>
        public static ReadResult Read(string path, bool wantSubs = true, string hive = null)
        {
            using (var k = Open(hive == null ? HKLM : Hive(hive), path))
            {
                if (k == null) return null;
                int nSubs, nVals; long last;
                var r = new ReadResult();
                if (!k.Info(out nSubs, out nVals, out last)) return r;
                for (int i = 0; i < nVals; i++)
                {
                    var v = k.EnumValue(i);
                    if (v == null) break;
                    var p = Plain(v.Data);
                    if (p != null) r.Vals[v.Name] = p;
                }
                if (wantSubs)
                    for (int i = 0; i < nSubs; i++) { var s = k.EnumKey(i); if (s == null) break; r.Subs.Add(s); }
                if (last != 0)
                {
                    try { r.Stamp = DateTime.FromFileTimeUtc(last).ToString("yyyy-MM-dd"); } catch { r.Stamp = ""; }
                }
                return r;
            }
        }

        /// <summary>show_data: Binärdaten als Hex-Paare (gekürzt), alles andere unverändert.</summary>
        public static object Show(object data, int limit = 64)
        {
            if (data is byte[] b)
            {
                int n = Math.Min(b.Length, limit);
                var sb = new StringBuilder();
                for (int i = 0; i < n; i++) { if (i > 0) sb.Append(' '); sb.Append(b[i].ToString("x2")); }
                if (b.Length > limit) sb.Append(" ... (" + b.Length + " Bytes)");
                return sb.ToString();
            }
            return data;
        }

        /// <summary>rget: Wert (über Show) oder def.</summary>
        public static object Get(string hive, string path, string name, object def = null)
        {
            using (var k = Open(Hive(hive), path))
            {
                if (k == null) return def;
                var v = k.Query(name ?? "");
                if (v == null) return def;
                return Show(v.Data);
            }
        }

        public static List<string> Subs(string hive, string path)
        {
            var r = Read(path, true, hive);
            return r != null ? r.Subs : new List<string>();
        }

        public static J Vals(string hive, string path)
        {
            var r = Read(path, false, hive);
            return r != null ? r.Vals : new J();
        }

        /// <summary>split_regpath: "HKLM\a\b" -> ("HKLM", "a\b").</summary>
        public static void Split(string s, out string head, out string rest)
        {
            s = s.Trim().Replace("/", "\\").Trim('\\');
            int i = s.IndexOf('\\');
            if (i < 0) { head = s; rest = ""; }
            else { head = s.Substring(0, i); rest = s.Substring(i + 1); }
        }
    }
}
