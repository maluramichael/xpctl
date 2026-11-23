using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace XpServer
{
    /// <summary>xpserver.json neben der Exe (siehe xpserver.example.json).</summary>
    public static class Cfg
    {
        public const string Name = "xpserver.json";
        public static J Data = new J();
        public static string PathUsed;

        public static void Load(string path)
        {
            Data = new J();
            PathUsed = path;
            if (path != null && File.Exists(path))
            {
                var o = Json.Parse(File.ReadAllText(path, Encoding.UTF8)) as J;
                if (o == null) throw new FormatException(Name + ": oberste Ebene muss ein Objekt sein");
                Data = o;
            }
        }

        public static object Get(string key)
        {
            object v;
            return Data.TryGetValue(key, out v) ? v : null;
        }

        public static string Str(string key)
        {
            var v = Get(key);
            return v == null ? null : Convert.ToString(v);
        }

        public static List<string> Strs(string key)
        {
            var r = new List<string>();
            var v = Get(key);
            if (v is List<object> l) foreach (var x in l) r.Add(Convert.ToString(x));
            else if (v != null) r.Add(Convert.ToString(v));
            return r;
        }

        /// <summary>cfg_need: Wert oder Fehler 'nicht konfiguriert'.</summary>
        public static string Need(string key, string what)
        {
            var v = Get(key);
            if (!Py.Truthy(v))
                throw new OpError(string.Format("nicht konfiguriert: \"{0}\" ({1}) in {2}", key, what, Name));
            return Convert.ToString(v);
        }
    }
}
