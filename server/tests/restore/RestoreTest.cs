using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using XpServer;

/// <summary>
/// Zurückholen eines Spielstand-Ordners aus einem Zip (Core/Restore.cs), mit dem echten Zip-Schreiber.
/// Fälle: geänderte Datei, später dazugekommene Datei (muss weg), gelöschte Datei in einem Unterordner
/// (kommt wieder), schreibgeschützte Datei, Probelauf ändert nichts, unsichere Namen werden abgelehnt.
/// Exit 0 = alles gut.
/// </summary>
static class RestoreTest
{
    static int _fails;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "ok    " : "FEHLER") + "  " + what);
        if (!ok) _fails++;
    }

    static byte[] B(string s) { return Encoding.UTF8.GetBytes(s); }

    static byte[] MakeZip(params (string name, byte[] data)[] files)
    {
        using (var ms = new MemoryStream())
        {
            var w = new Zip.Writer(ms);
            foreach (var f in files) w.Add(f.name, f.data, new DateTime(2026, 11, 27, 21, 0, 0));
            w.Add(Restore.InfoName, B("{\"game\": \"d2\"}"), DateTime.Now);
            w.Finish();
            return ms.ToArray();
        }
    }

    static int Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "xpctl-restore-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Stand im Zip: zwei Charaktere oben, einer im Unterordner, eine große komprimierbare Datei
            var big = B(new string('x', 50000));
            var zip = MakeZip(("Held.d2s", B("held v1")), ("Magier.d2s", B("magier v1")), ("Europe/Bote.d2s", B("bote v1")), ("big.bin", big));
            byte[] info;
            var files = Restore.Load(zip, out info);
            Check(files.Count == 4 && info != null && Encoding.UTF8.GetString(info).Contains("d2"), "Load: 4 Dateien und info.json getrennt");

            // heutiger Ordner: Held geändert, Magier schreibgeschützt und geändert, Bote gelöscht, Neu.d2s dazu
            Directory.CreateDirectory(Path.Combine(dir, "Europe"));
            File.WriteAllBytes(Path.Combine(dir, "Held.d2s"), B("held v2, weiter gespielt"));
            File.WriteAllBytes(Path.Combine(dir, "Magier.d2s"), B("magier v2"));
            File.SetAttributes(Path.Combine(dir, "Magier.d2s"), FileAttributes.ReadOnly);
            File.WriteAllBytes(Path.Combine(dir, "Neu.d2s"), B("später angelegt"));
            File.WriteAllBytes(Path.Combine(dir, "big.bin"), big);

            var dry = Restore.Apply(dir, files, null, true);
            Check(dry.Written.Count == 3 && dry.Removed.Count == 1 && dry.Same.Count == 1, "Probelauf: 3 schreiben, 1 löschen, 1 gleich");
            Check(File.ReadAllText(Path.Combine(dir, "Held.d2s")) == "held v2, weiter gespielt" && File.Exists(Path.Combine(dir, "Neu.d2s")),
                "Probelauf ändert nichts");
            Check(Restore.Verify(dir, files).Count == 4, "Verify meldet vorher 4 Abweichungen");

            var t = new DateTime(2026, 11, 20, 18, 30, 15);
            var res = Restore.Apply(dir, files, new Dictionary<string, DateTime> { { "Held.d2s", t } });
            Check(res.Removed.Count == 1 && res.Removed[0] == "Neu.d2s" && !File.Exists(Path.Combine(dir, "Neu.d2s")), "später angelegte Datei gelöscht");
            Check(File.ReadAllText(Path.Combine(dir, "Europe", "Bote.d2s")) == "bote v1", "gelöschte Datei im Unterordner wieder da");
            Check(File.ReadAllText(Path.Combine(dir, "Magier.d2s")) == "magier v1", "schreibgeschützte Datei ersetzt");
            Check(File.GetLastWriteTime(Path.Combine(dir, "Held.d2s")) == t, "Änderungszeit aus info.json gesetzt");
            Check(res.Same.Count == 1 && res.Same[0] == "big.bin", "gleiche Datei nicht angefasst");
            Check(Restore.Verify(dir, files).Count == 0, "Verify: Ordner Byte für Byte gleich dem Zip");
            Check(Directory.GetFiles(dir, "*.xpctl-restore", SearchOption.AllDirectories).Length == 0, "keine Zwischendateien übrig");

            var again = Restore.Apply(dir, files, null);
            Check(again.Written.Count == 0 && again.Removed.Count == 0 && again.Same.Count == 4, "zweiter Lauf ändert nichts");

            foreach (var evil in new[] { "../x.d2s", "/etc/x", "C:/x", "a//b", "Europe/./x", "x.xpctl-restore" })
            {
                bool refused = false;
                try { Restore.Load(MakeZip((evil, B("böse"))), out info); } catch (IOException) { refused = true; }
                Check(refused, "unsicherer Name abgelehnt: " + evil);
            }
            var broken = (byte[])zip.Clone();
            broken[40] ^= 0xFF;                    // im ersten Dateiinhalt
            bool bad = false;
            try { Restore.Load(broken, out info); } catch (IOException) { bad = true; }
            Check(bad, "kaputtes Zip abgelehnt (CRC)");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(dir, true);
            }
        }
        Console.WriteLine(_fails == 0 ? "alles gut" : _fails + " Fehler");
        return _fails == 0 ? 0 : 1;
    }
}
