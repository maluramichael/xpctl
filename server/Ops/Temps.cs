using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace XpServer.Ops
{
    /// <summary>
    /// temps: Temperaturen aus mehreren Quellen. Jede Quelle ist eine Klasse mit Name und Read();
    /// eine neue Quelle = neue Klasse + Eintrag in <see cref="Sources"/>. Fehlt eine Quelle auf dem
    /// Rechner (kein ACPI-Sensor, keine NVIDIA-Karte, Programm läuft nicht), meldet sie das unter
    /// "sources", die anderen liefern trotzdem.
    /// </summary>
    public static class Temps
    {
        static readonly TempSource[] Sources =
        {
            new AcpiSource(),
            new SmartSource(),
            new NvidiaSource(),
            new HwinfoSource(),
            new SpeedFanSource(),
        };

        [Op("temps")]
        static J Run(Conn c, Req r)
        {
            var only = r.Strs("sources");
            var sensors = new List<object>();
            var info = new List<object>();
            foreach (var s in Sources)
            {
                if (only.Count > 0 && !only.Contains(s.Name)) continue;
                var got = new List<J>();
                var st = J.O("name", s.Name);
                try
                {
                    s.Read(got);
                    st["count"] = (long)got.Count;
                }
                catch (Unavailable u) { st["count"] = 0L; st["note"] = u.Message; }
                catch (Exception e) { st["count"] = 0L; st["error"] = e.GetType().Name + ": " + e.Message; }
                foreach (var g in got) sensors.Add(g);
                info.Add(st);
            }
            return J.Ok("sensors", sensors, "sources", info);
        }

        /// <summary>Ein Messwert. kind: cpu, gpu, board, disk, other. warn/crit nur, wenn die Quelle Grenzen kennt.</summary>
        internal static J Sensor(string source, string name, string kind, double celsius, double? warn = null, double? crit = null)
        {
            var j = J.O("source", source, "name", name, "kind", kind, "celsius", Math.Round(celsius, 1));
            if (warn.HasValue) j["warn"] = Math.Round(warn.Value, 1);
            if (crit.HasValue) j["crit"] = Math.Round(crit.Value, 1);
            return j;
        }
    }

    /// <summary>Quelle gibt es auf diesem Rechner nicht (kein Fehler, nur ein Hinweis).</summary>
    public class Unavailable : Exception { public Unavailable(string m) : base(m) { } }

    public abstract class TempSource
    {
        public abstract string Name { get; }
        public abstract void Read(List<J> into);
    }

    // ---------------------------------------------------------------- ACPI (Mainboard)

    /// <summary>ACPI-Thermalzonen per WMI (root\WMI, MSAcpi_ThermalZoneTemperature). Werte in Zehntel-Kelvin.</summary>
    public class AcpiSource : TempSource
    {
        public override string Name { get { return "acpi"; } }

        static double C(object tenthKelvin) { return Convert.ToDouble(tenthKelvin, CultureInfo.InvariantCulture) / 10.0 - 273.15; }

        public override void Read(List<J> into)
        {
            try
            {
                using (var q = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM MSAcpi_ThermalZoneTemperature"))
                    foreach (ManagementObject o in q.Get())
                    {
                        string inst = Convert.ToString(o["InstanceName"]);
                        string name = inst.Substring(inst.LastIndexOf('\\') + 1);
                        double? warn = null, crit = null;
                        if (o["PassiveTripPoint"] != null && Convert.ToInt64(o["PassiveTripPoint"]) > 0) warn = C(o["PassiveTripPoint"]);
                        if (o["CriticalTripPoint"] != null && Convert.ToInt64(o["CriticalTripPoint"]) > 0) crit = C(o["CriticalTripPoint"]);
                        into.Add(Temps.Sensor(Name, "Thermalzone " + name, "board", C(o["CurrentTemperature"]), warn, crit));
                    }
            }
            catch (ManagementException e) { throw new Unavailable("keine ACPI-Thermalzone (" + e.ErrorCode + ")"); }
            if (into.Count == 0) throw new Unavailable("keine ACPI-Thermalzone");
        }
    }

    // ---------------------------------------------------------------- SMART (Festplatten, SSDs)

    /// <summary>SMART-Attribut 194 (sonst 190) aus root\WMI MSStorageDriver_ATAPISmartData, Name aus Win32_DiskDrive.</summary>
    public class SmartSource : TempSource
    {
        public override string Name { get { return "smart"; } }

        public override void Read(List<J> into)
        {
            var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var q = new ManagementObjectSearcher("SELECT PNPDeviceID, Model FROM Win32_DiskDrive"))
                foreach (ManagementObject o in q.Get())
                    if (o["PNPDeviceID"] != null) models[Convert.ToString(o["PNPDeviceID"])] = Convert.ToString(o["Model"]).Trim();
            try
            {
                using (var q = new ManagementObjectSearcher(@"root\WMI", "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_ATAPISmartData"))
                    foreach (ManagementObject o in q.Get())
                    {
                        var b = o["VendorSpecific"] as byte[];
                        if (b == null) continue;
                        int? t = null;
                        for (int i = 2; i + 12 <= b.Length; i += 12)   // 30 Einträge à 12 Byte ab Offset 2
                        {
                            if (b[i] == 194) { t = b[i + 5]; break; }
                            if (b[i] == 190 && t == null) t = b[i + 5];
                        }
                        if (t == null) continue;
                        string inst = Convert.ToString(o["InstanceName"]);
                        string key = inst.EndsWith("_0") ? inst.Substring(0, inst.Length - 2) : inst;
                        string name;
                        if (!models.TryGetValue(key, out name)) name = inst;
                        into.Add(Temps.Sensor(Name, name, "disk", t.Value));
                    }
            }
            catch (ManagementException e) { throw new Unavailable("SMART nicht lesbar (" + e.ErrorCode + ")"); }
            if (into.Count == 0) throw new Unavailable("kein Laufwerk meldet eine Temperatur");
        }
    }

    // ---------------------------------------------------------------- NVIDIA (NVAPI)

    /// <summary>GPU-Temperatur über nvapi.dll (NvAPI_GPU_GetThermalSettings), ab NVIDIA-Treiber ca. 2007.</summary>
    public class NvidiaSource : TempSource
    {
        public override string Name { get { return "nvidia"; } }

        [DllImport("nvapi.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr QueryInterface(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumFn([Out] IntPtr[] handles, out int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NameFn(IntPtr gpu, StringBuilder name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ThermalFn(IntPtr gpu, int sensorIndex, IntPtr settings);

        static T Fn<T>(uint id) where T : class
        {
            IntPtr p = QueryInterface(id);
            if (p == IntPtr.Zero) throw new Unavailable("NVAPI-Funktion 0x" + id.ToString("X8") + " fehlt im Treiber");
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }


        public override void Read(List<J> into)
        {
            try { QueryInterface(0); }
            catch (DllNotFoundException) { throw new Unavailable("kein NVIDIA-Treiber (nvapi.dll fehlt)"); }
            catch (EntryPointNotFoundException) { throw new Unavailable("nvapi.dll ohne nvapi_QueryInterface"); }

            if (Fn<InitFn>(0x0150E828)() != 0) throw new Unavailable("NvAPI_Initialize fehlgeschlagen");
            var handles = new IntPtr[64];
            int n;
            if (Fn<EnumFn>(0xE5AC921F)(handles, out n) != 0 || n == 0) throw new Unavailable("keine NVIDIA-GPU gefunden");
            var getName = Fn<NameFn>(0xCEEE8E9F);
            var thermal = Fn<ThermalFn>(0xE3640A56);

            // NV_GPU_THERMAL_SETTINGS: version, count, 3 x {controller, min, max, current, target} = 68 Byte
            const int Size = 8 + 3 * 20;
            IntPtr buf = Marshal.AllocHGlobal(Size);
            try
            {
                for (int g = 0; g < n; g++)
                {
                    var sb = new StringBuilder(64);
                    string gpu = getName(handles[g], sb) == 0 ? sb.ToString() : "GPU " + g;
                    int rc = -1;
                    foreach (int ver in new[] { 2, 1 })
                    {
                        for (int i = 0; i < Size; i += 4) Marshal.WriteInt32(buf, i, 0);
                        Marshal.WriteInt32(buf, 0, Size | (ver << 16));
                        rc = thermal(handles[g], 15, buf);     // 15 = alle Sensoren
                        if (rc == 0) break;
                    }
                    if (rc != 0) continue;
                    int count = Math.Min(3, Marshal.ReadInt32(buf, 4));
                    for (int s = 0; s < count; s++)
                    {
                        int o = 8 + s * 20;
                        int cur = Marshal.ReadInt32(buf, o + 12), target = Marshal.ReadInt32(buf, o + 16);
                        string t = target == 1 ? "GPU" : target == 2 ? "Speicher" : target == 4 ? "Netzteil" : target == 8 ? "Board" : "Sensor " + s;
                        // defaultMaxTemp ist nur das Ende des Messbereichs (meist 127), keine Warngrenze: nicht melden
                        into.Add(Temps.Sensor(Name, gpu + " " + t, target == 1 ? "gpu" : "other", cur));
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            if (into.Count == 0) throw new Unavailable("NVIDIA-Treiber liefert keine Temperatur");
        }
    }

    // ---------------------------------------------------------------- Shared Memory von Monitoring-Programmen

    static class SharedMem
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr MapViewOfFile(IntPtr h, uint access, uint hi, uint lo, UIntPtr bytes);
        [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr p);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        /// <summary>Öffnet eine benannte Freigabe nur lesend; null, wenn es sie nicht gibt (Programm läuft nicht).</summary>
        public static T Read<T>(string[] names, Func<IntPtr, T> read) where T : class
        {
            foreach (var n in names)
            {
                IntPtr h = OpenFileMappingW(4 /* FILE_MAP_READ */, false, n);
                if (h == IntPtr.Zero) continue;
                try
                {
                    IntPtr p = MapViewOfFile(h, 4, 0, 0, UIntPtr.Zero);
                    if (p == IntPtr.Zero) continue;
                    try { return read(p); }
                    finally { UnmapViewOfFile(p); }
                }
                finally { CloseHandle(h); }
            }
            return null;
        }

        public static string Ansi(IntPtr p, int off, int len)
        {
            var b = new byte[len];
            Marshal.Copy(new IntPtr(p.ToInt64() + off), b, 0, len);
            int z = Array.IndexOf(b, (byte)0);
            return Encoding.Default.GetString(b, 0, z < 0 ? len : z).Trim();
        }

        public static double Dbl(IntPtr p, int off) { return BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p, off)); }
    }

    /// <summary>HWiNFO (Einstellung "Shared Memory Support" an): alle Temperatur-Werte aller Sensoren.</summary>
    public class HwinfoSource : TempSource
    {
        public override string Name { get { return "hwinfo"; } }

        public override void Read(List<J> into)
        {
            var res = SharedMem.Read(new[] { @"Global\HWiNFO_SENS_SM2", "HWiNFO_SENS_SM2" }, p =>
            {
                uint sig = (uint)Marshal.ReadInt32(p, 0);
                if (sig == 0x44414544) throw new Unavailable("HWiNFO läuft, Shared Memory ist aber abgelaufen oder aus");
                if (sig != 0x53695748) throw new Unavailable("HWiNFO-Freigabe mit unbekannter Signatur");
                // Kopf: sig, version, revision, poll_time (8), dann je Offset/Größe/Anzahl für Sensoren und Werte
                int sOff = Marshal.ReadInt32(p, 20), sSize = Marshal.ReadInt32(p, 24), sNum = Marshal.ReadInt32(p, 28);
                int rOff = Marshal.ReadInt32(p, 32), rSize = Marshal.ReadInt32(p, 36), rNum = Marshal.ReadInt32(p, 40);
                var sensorNames = new List<string>();
                for (int i = 0; i < sNum; i++)
                {
                    int o = sOff + i * sSize;
                    string user = SharedMem.Ansi(p, o + 8 + 128, 128), orig = SharedMem.Ansi(p, o + 8, 128);
                    sensorNames.Add(user.Length > 0 ? user : orig);
                }
                var list = new List<J>();
                for (int i = 0; i < rNum; i++)
                {
                    int o = rOff + i * rSize;
                    if (Marshal.ReadInt32(p, o) != 1) continue;          // 1 = Temperatur
                    int si = Marshal.ReadInt32(p, o + 4);
                    string user = SharedMem.Ansi(p, o + 12 + 128, 128), orig = SharedMem.Ansi(p, o + 12, 128);
                    string unit = SharedMem.Ansi(p, o + 12 + 256, 16);
                    double v = SharedMem.Dbl(p, o + 12 + 256 + 16);
                    if (unit.IndexOf('F') >= 0) v = (v - 32) * 5 / 9;
                    string sensor = si >= 0 && si < sensorNames.Count ? sensorNames[si] : "?";
                    string label = user.Length > 0 ? user : orig;
                    string s = (sensor + " " + label).ToLowerInvariant();
                    string kind = s.Contains("gpu") ? "gpu" : s.Contains("cpu") || s.Contains("core") ? "cpu" :
                                  s.Contains("drive") || s.Contains("disk") || s.Contains("ssd") || s.Contains("smart") ? "disk" : "board";
                    list.Add(Temps.Sensor(Name, sensor + ": " + label, kind, v));
                }
                return list;
            });
            if (res == null) throw new Unavailable("HWiNFO läuft nicht (oder Shared Memory Support aus)");
            into.AddRange(res);
        }
    }

    /// <summary>SpeedFan (läuft): Temperaturen aus SFSharedMemory_ALM. SpeedFan liefert keine Namen, nur Nummern.</summary>
    public class SpeedFanSource : TempSource
    {
        public override string Name { get { return "speedfan"; } }

        public override void Read(List<J> into)
        {
            var res = SharedMem.Read(new[] { "SFSharedMemory_ALM" }, p =>
            {
                // version(2), flags(2), MemSize(4), handle(4), NumTemps(2), NumFans(2), NumVolts(2), temps[32] (x100)
                int n = Math.Min(32, (int)(ushort)Marshal.ReadInt16(p, 12));
                var list = new List<J>();
                for (int i = 0; i < n; i++)
                {
                    double t = Marshal.ReadInt32(p, 18 + 4 * i) / 100.0;
                    if (t <= 0 || t > 150) continue;   // unbelegter Eingang (SpeedFan meldet dann z. B. -64 oder 0)
                    list.Add(Temps.Sensor(Name, "Temp" + (i + 1), "other", t));
                }
                return list;
            });
            if (res == null) throw new Unavailable("SpeedFan läuft nicht");
            into.AddRange(res);
        }
    }
}
