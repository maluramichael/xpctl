using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace XpServer
{
    /// <summary>Objekt im JSON-Protokoll: Schlüssel in Einfügereihenfolge (wie Python-dict).</summary>
    public class J : Dictionary<string, object>
    {
        public J() { }
        public J(IDictionary<string, object> d) : base(d) { }

        /// <summary>J.O("ok", true, "pid", 12): Schlüssel/Wert-Paare.</summary>
        public static J O(params object[] kv)
        {
            var j = new J();
            for (int i = 0; i + 1 < kv.Length; i += 2) j[(string)kv[i]] = kv[i + 1];
            return j;
        }

        public static J Ok(params object[] kv)
        {
            var j = new J();
            j["ok"] = true;
            for (int i = 0; i + 1 < kv.Length; i += 2) j[(string)kv[i]] = kv[i + 1];
            return j;
        }

        public static J Err(string msg) { return O("ok", false, "error", msg); }
    }

    /// <summary>Minimaler JSON-Parser/-Schreiber, verhält sich wie Pythons json (dumps mit ensure_ascii).</summary>
    public static class Json
    {
        // ---------- Schreiben ----------

        public static string Dump(object o)
        {
            var sb = new StringBuilder();
            Write(sb, o);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object o)
        {
            if (o == null) { sb.Append("null"); return; }
            if (o is string s) { Str(sb, s); return; }
            if (o is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (o is char c) { Str(sb, c.ToString()); return; }
            if (o is int || o is long || o is short || o is byte || o is sbyte || o is ushort)
            { sb.Append(Convert.ToInt64(o).ToString(CultureInfo.InvariantCulture)); return; }
            if (o is uint || o is ulong)
            { sb.Append(Convert.ToUInt64(o).ToString(CultureInfo.InvariantCulture)); return; }
            if (o is double || o is float || o is decimal) { Num(sb, Convert.ToDouble(o)); return; }
            if (o is IDictionary<string, object> d)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in d)
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    Str(sb, kv.Key);
                    sb.Append(": ");
                    Write(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            if (o is IDictionary nd)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry kv in nd)
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    Str(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                    sb.Append(": ");
                    Write(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            if (o is byte[]) throw new ArgumentException("byte[] ist kein JSON (vorher in Hex/Base64 wandeln)");
            if (o is IEnumerable e)
            {
                sb.Append('[');
                bool first = true;
                foreach (var x in e)
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    Write(sb, x);
                }
                sb.Append(']');
                return;
            }
            throw new ArgumentException("nicht JSON-fähig: " + o.GetType().Name);
        }

        static void Num(StringBuilder sb, double v)
        {
            if (double.IsNaN(v)) { sb.Append("NaN"); return; }
            if (double.IsPositiveInfinity(v)) { sb.Append("Infinity"); return; }
            if (double.IsNegativeInfinity(v)) { sb.Append("-Infinity"); return; }
            string r = v.ToString("R", CultureInfo.InvariantCulture);
            if (r.IndexOf('E') >= 0)
            {
                // Python: 1e-05, 1e+20
                r = r.Replace("E", "e");
                int i = r.IndexOf('e');
                if (r[i + 1] != '-' && r[i + 1] != '+') r = r.Insert(i + 1, "+");
                string mant = r.Substring(0, i), exp = r.Substring(i + 2);
                if (exp.Length < 2) exp = "0" + exp;
                r = mant + "e" + r[i + 1] + exp;
            }
            else if (r.IndexOf('.') < 0) r += ".0";
            sb.Append(r);
        }

        static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------- Lesen ----------

        public static object Parse(string s)
        {
            int i = 0;
            object v = Value(s, ref i);
            Ws(s, ref i);
            if (i != s.Length) throw new FormatException("JSON: Zeichen nach dem Ende bei " + i);
            return v;
        }

        static void Ws(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON: unerwartetes Ende");
            char c = s[i];
            if (c == '{')
            {
                var d = new J();
                i++;
                Ws(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i);
                    string k = StrVal(s, ref i);
                    Ws(s, ref i);
                    Expect(s, ref i, ':');
                    d[k] = Value(s, ref i);
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, '}');
                    return d;
                }
            }
            if (c == '[')
            {
                var l = new List<object>();
                i++;
                Ws(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i));
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    Expect(s, ref i, ']');
                    return l;
                }
            }
            if (c == '"') return StrVal(s, ref i);
            if (Lit(s, ref i, "true")) return true;
            if (Lit(s, ref i, "false")) return false;
            if (Lit(s, ref i, "null")) return null;
            if (Lit(s, ref i, "NaN")) return double.NaN;
            if (Lit(s, ref i, "Infinity")) return double.PositiveInfinity;
            if (Lit(s, ref i, "-Infinity")) return double.NegativeInfinity;
            int st = i;
            bool isFloat = false;
            if (s[i] == '-') i++;
            while (i < s.Length)
            {
                char x = s[i];
                if (x >= '0' && x <= '9') { i++; continue; }
                if (x == '.' || x == 'e' || x == 'E' || x == '+' || x == '-') { isFloat = true; i++; continue; }
                break;
            }
            string num = s.Substring(st, i - st);
            if (num.Length == 0 || num == "-") throw new FormatException("JSON: unerwartetes Zeichen bei " + st);
            if (!isFloat)
            {
                long lv;
                if (long.TryParse(num, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out lv)) return lv;
            }
            return double.Parse(num, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static bool Lit(string s, ref int i, string lit)
        {
            if (string.CompareOrdinal(s, i, lit, 0, lit.Length) == 0) { i += lit.Length; return true; }
            return false;
        }

        static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c) throw new FormatException("JSON: '" + c + "' erwartet bei " + i);
            i++;
        }

        static string StrVal(string s, ref int i)
        {
            Expect(s, ref i, '"');
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("JSON: offener String");
                char c = s[i++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>Lesezugriff auf eine Anfrage mit Python-ähnlichen Defaults (req.get("x", d)).</summary>
    public class Req
    {
        public readonly J Raw;
        public Req(J raw) { Raw = raw; }

        public string Op { get { return Str("op"); } }
        public bool Has(string k) { return Raw.ContainsKey(k) && Raw[k] != null; }
        public object this[string k] { get { object v; return Raw.TryGetValue(k, out v) ? v : null; } }

        /// <summary>Wie req[k] in Python: fehlt der Schlüssel, KeyError.</summary>
        public string Need(string k)
        {
            if (!Raw.ContainsKey(k)) throw new OpError("KeyError", "'" + k + "'");
            return Convert.ToString(Raw[k], CultureInfo.InvariantCulture);
        }

        public string Str(string k, string def = null)
        {
            object v = this[k];
            if (v == null) return def;
            if (v is bool b) return b ? "True" : "False";
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        /// <summary>req.get(k) or def: leere Strings/0/False zählen als fehlend.</summary>
        public string Or(string k, string def)
        {
            object v = this[k];
            if (!Py.Truthy(v)) return def;
            return Str(k);
        }

        public long Int(string k, long def = 0)
        {
            object v = this[k];
            if (v == null) return def;
            if (v is long l) return l;
            if (v is double d) return (long)d;
            if (v is bool b) return b ? 1 : 0;
            return long.Parse(Convert.ToString(v, CultureInfo.InvariantCulture).Trim(), CultureInfo.InvariantCulture);
        }

        public double Dbl(string k, double def = 0)
        {
            object v = this[k];
            if (v == null) return def;
            if (v is long l) return l;
            if (v is double d) return d;
            return double.Parse(Convert.ToString(v, CultureInfo.InvariantCulture).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        /// <summary>bool(req.get(k)).</summary>
        public bool Bool(string k) { return Py.Truthy(this[k]); }

        public List<object> List(string k)
        {
            object v = this[k];
            if (v == null) return new List<object>();
            if (v is List<object> l) return l;
            return new List<object> { v };
        }

        public List<string> Strs(string k)
        {
            var r = new List<string>();
            foreach (var x in List(k)) r.Add(Convert.ToString(x, CultureInfo.InvariantCulture));
            return r;
        }
    }

    /// <summary>Fehler aus einer Op: wird wie in Python als "Typ: Meldung" an den Client gemeldet.</summary>
    public class OpError : Exception
    {
        public readonly string PyType;
        public OpError(string msg) : base(msg) { PyType = "ValueError"; }
        public OpError(string pyType, string msg) : base(msg) { PyType = pyType; }
    }

    public static class Py
    {
        /// <summary>Stabile Sortierung wie Pythons sorted(key=...), Vergleich nach Codepunkt.</summary>
        public static List<T> SortBy<T>(IEnumerable<T> items, Func<T, string> key)
        {
            return System.Linq.Enumerable.ToList(System.Linq.Enumerable.OrderBy(items, key, StringComparer.Ordinal));
        }

        public static bool Truthy(object v)
        {
            if (v == null) return false;
            if (v is bool b) return b;
            if (v is string s) return s.Length > 0;
            if (v is long l) return l != 0;
            if (v is int i) return i != 0;
            if (v is double d) return d != 0;
            if (v is ICollection c) return c.Count > 0;
            return true;
        }

        /// <summary>str(x) wie Python (für Log-Zeilen).</summary>
        public static string Repr(object v)
        {
            if (v == null) return "None";
            if (v is bool b) return b ? "True" : "False";
            if (v is string s) return s;
            if (v is double d) { var sb = Json.Dump(d); return sb; }
            if (v is IDictionary<string, object> || (v is IEnumerable && !(v is string)))
            {
                return ReprInner(v);
            }
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        static string ReprInner(object v)
        {
            if (v == null) return "None";
            if (v is bool b) return b ? "True" : "False";
            if (v is string s) return "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
            if (v is IDictionary<string, object> d)
            {
                var parts = new List<string>();
                foreach (var kv in d) parts.Add(ReprInner(kv.Key) + ": " + ReprInner(kv.Value));
                return "{" + string.Join(", ", parts.ToArray()) + "}";
            }
            if (v is IEnumerable e)
            {
                var parts = new List<string>();
                foreach (var x in e) parts.Add(ReprInner(x));
                return "[" + string.Join(", ", parts.ToArray()) + "]";
            }
            return Repr(v);
        }
    }
}
