using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace XpServer
{
    /// <summary>
    /// Kleiner Zip-Schreiber und -Leser (.NET 3.5 hat kein Zip). Bewusst schlicht, damit auch der
    /// Explorer von XP ("ZIP-komprimierte Ordner") die Dateien öffnet: jede Datei erst komplett in den
    /// Speicher gepackt, Kopf mit echter CRC und echten Größen (kein Datendeskriptor), kein Zip64, keine
    /// Extra-Felder. Deflate nur, wenn es kleiner wird, sonst gespeichert. Namen mit '/', UTF-8-Flag nur
    /// bei Nicht-ASCII-Namen. Für Spielstände gedacht: eine Datei und das ganze Zip bleiben unter 2 GB.
    /// </summary>
    public static class Zip
    {
        public sealed class Entry
        {
            public string Name;
            public uint Crc;
            public long Size, Packed, Offset;
            public int Method;           // 0 gespeichert, 8 Deflate
            public DateTime Time;
            public bool Utf8;
        }

        static readonly uint[] Table = MakeTable();

        static uint[] MakeTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        public static uint Crc32(byte[] data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        static bool Ascii(string s) { foreach (char ch in s) if (ch > 126 || ch < 32) return false; return true; }

        static void DosTime(DateTime t, out ushort time, out ushort date)
        {
            if (t.Year < 1980) t = new DateTime(1980, 1, 1);
            if (t.Year > 2107) t = new DateTime(2107, 12, 31, 23, 59, 58);
            time = (ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2));
            date = (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day);
        }

        static DateTime FromDos(ushort time, ushort date)
        {
            try { return new DateTime(1980 + (date >> 9), (date >> 5) & 15, date & 31, time >> 11, (time >> 5) & 63, (time & 31) * 2); }
            catch { return new DateTime(1980, 1, 1); }
        }

        static void W16(Stream s, int v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); }
        static void W32(Stream s, long v) { W16(s, (int)(v & 0xFFFF)); W16(s, (int)((v >> 16) & 0xFFFF)); }

        /// <summary>Schreibt ein Zip in <paramref name="output"/> (wird nicht geschlossen).</summary>
        public sealed class Writer
        {
            readonly Stream _out;
            readonly List<Entry> _entries = new List<Entry>();
            long _pos;

            public Writer(Stream output) { _out = output; }

            public Entry Add(string name, byte[] data, DateTime time)
            {
                name = name.Replace('\\', '/');
                byte[] packed;
                using (var ms = new MemoryStream())
                {
                    using (var d = new DeflateStream(ms, CompressionMode.Compress, true)) d.Write(data, 0, data.Length);
                    packed = ms.ToArray();
                }
                var e = new Entry { Name = name, Crc = Crc32(data), Size = data.Length, Time = time, Offset = _pos, Utf8 = !Ascii(name) };
                byte[] body = data;
                if (packed.Length < data.Length) { body = packed; e.Method = 8; }
                e.Packed = body.Length;
                if (_pos + body.Length > int.MaxValue) throw new IOException("Zip würde größer als 2 GB");
                byte[] nm = (e.Utf8 ? Encoding.UTF8 : Encoding.ASCII).GetBytes(name);
                var h = new MemoryStream();
                W32(h, 0x04034b50);
                W16(h, 20);                         // benötigte Version 2.0
                W16(h, e.Utf8 ? 0x0800 : 0);
                W16(h, e.Method);
                ushort t, d0; DosTime(time, out t, out d0);
                W16(h, t); W16(h, d0);
                W32(h, e.Crc); W32(h, e.Packed); W32(h, e.Size);
                W16(h, nm.Length); W16(h, 0);
                h.Write(nm, 0, nm.Length);
                Put(h.ToArray());
                Put(body);
                _entries.Add(e);
                return e;
            }

            void Put(byte[] b) { _out.Write(b, 0, b.Length); _pos += b.Length; }

            /// <summary>Zentralverzeichnis und Abschluss schreiben.</summary>
            public void Finish()
            {
                long cdStart = _pos;
                var h = new MemoryStream();
                foreach (var e in _entries)
                {
                    byte[] nm = (e.Utf8 ? Encoding.UTF8 : Encoding.ASCII).GetBytes(e.Name);
                    W32(h, 0x02014b50);
                    W16(h, 20); W16(h, 20);
                    W16(h, e.Utf8 ? 0x0800 : 0);
                    W16(h, e.Method);
                    ushort t, d0; DosTime(e.Time, out t, out d0);
                    W16(h, t); W16(h, d0);
                    W32(h, e.Crc); W32(h, e.Packed); W32(h, e.Size);
                    W16(h, nm.Length); W16(h, 0); W16(h, 0);   // Name, Extra, Kommentar
                    W16(h, 0); W16(h, 0); W32(h, 0);           // Disk, interne und externe Attribute
                    W32(h, e.Offset);
                    h.Write(nm, 0, nm.Length);
                }
                if (_entries.Count > 0xFFFF) throw new IOException("zu viele Dateien für ein Zip ohne Zip64");
                long cdLen = h.Length;
                W32(h, 0x06054b50);
                W16(h, 0); W16(h, 0);
                W16(h, _entries.Count); W16(h, _entries.Count);
                W32(h, cdLen); W32(h, cdStart);
                W16(h, 0);
                Put(h.ToArray());
                _out.Flush();
            }
        }

        static int R16(byte[] b, long i) { return b[i] | (b[i + 1] << 8); }
        static uint R32(byte[] b, long i) { return (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24)); }

        /// <summary>Einträge aus dem Zentralverzeichnis. Wirft IOException, wenn die Datei kein lesbares Zip ist.</summary>
        public static List<Entry> List(byte[] z)
        {
            long eocd = -1;
            for (long i = z.Length - 22; i >= 0 && i >= z.Length - 22 - 0xFFFF; i--)
                if (R32(z, i) == 0x06054b50) { eocd = i; break; }
            if (eocd < 0) throw new IOException("kein Zip (Abschluss fehlt)");
            int count = R16(z, eocd + 10);
            long p = R32(z, eocd + 16);
            var l = new List<Entry>();
            for (int n = 0; n < count; n++)
            {
                if (p + 46 > z.Length || R32(z, p) != 0x02014b50) throw new IOException("Zentralverzeichnis kaputt");
                int flags = R16(z, p + 8);
                int nlen = R16(z, p + 28), xlen = R16(z, p + 30), clen = R16(z, p + 32);
                var e = new Entry
                {
                    Method = R16(z, p + 10),
                    Time = FromDos((ushort)R16(z, p + 12), (ushort)R16(z, p + 14)),
                    Crc = R32(z, p + 16),
                    Packed = R32(z, p + 20),
                    Size = R32(z, p + 24),
                    Offset = R32(z, p + 42),
                    Utf8 = (flags & 0x0800) != 0,
                };
                e.Name = (e.Utf8 ? Encoding.UTF8 : Encoding.Default).GetString(z, (int)p + 46, nlen);
                l.Add(e);
                p += 46 + nlen + xlen + clen;
            }
            return l;
        }

        /// <summary>Inhalt eines Eintrags; prüft Kopf, Größe und CRC.</summary>
        public static byte[] Read(byte[] z, Entry e)
        {
            long p = e.Offset;
            if (p + 30 > z.Length || R32(z, p) != 0x04034b50) throw new IOException(e.Name + ": Dateikopf fehlt");
            long start = p + 30 + R16(z, p + 26) + R16(z, p + 28);
            if (start + e.Packed > z.Length) throw new IOException(e.Name + ": abgeschnitten");
            byte[] data;
            if (e.Method == 0)
            {
                data = new byte[e.Packed];
                Array.Copy(z, start, data, 0, e.Packed);
            }
            else if (e.Method == 8)
            {
                using (var src = new MemoryStream(z, (int)start, (int)e.Packed))
                using (var d = new DeflateStream(src, CompressionMode.Decompress))
                using (var ms = new MemoryStream())
                {
                    var buf = new byte[65536];
                    int n;
                    while ((n = d.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                    data = ms.ToArray();
                }
            }
            else throw new IOException(e.Name + ": unbekannte Packmethode " + e.Method);
            if (data.Length != e.Size || Crc32(data) != e.Crc) throw new IOException(e.Name + ": Prüfsumme stimmt nicht");
            return data;
        }
    }
}
