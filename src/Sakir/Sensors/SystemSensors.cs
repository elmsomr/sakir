using System.Runtime.InteropServices;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using Sakir.Core;

namespace Sakir.Sensors;

/// <summary>
/// CPU and memory readings.
///
/// Load and RAM come from Win32 calls that work for any user and on any system
/// locale - deliberately NOT from performance counters, whose names are
/// localised and do not resolve on a non-English Windows.
///
/// CPU temperature is the one thing that needs LibreHardwareMonitor, and LHM
/// needs to load a kernel driver, which needs elevation. Unelevated we simply
/// report CpuTempAvailable = false rather than failing.
/// </summary>
public sealed class SystemSensors : IDisposable
{
    private Computer? _computer;
    private readonly UpdateVisitor _visitor = new();

    public bool CpuTempAvailable { get; private set; }
    public float CpuTemp { get; private set; }
    public float CpuLoad { get; private set; }
    public int CpuClockMhz { get; private set; }
    public double RamUsedGb { get; private set; }
    public double RamTotalGb { get; private set; }
    public int RamPercent { get; private set; }

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public void Start()
    {
        if (!IsElevated) return;   // LHM's driver would just fail to load
        try
        {
            _computer = new Computer { IsCpuEnabled = true };
            _computer.Open();
            // Opening the library proves nothing about temperatures: without a
            // working driver it opens fine and reads none. Only Update() may
            // set this, once a real temperature value has been seen.
            CpuTempAvailable = false;
        }
        catch
        {
            _computer = null;
            CpuTempAvailable = false;
        }
    }

    private bool _loggedSensors;

    public void Update()
    {
        UpdateLoad();
        UpdateMemory();

        if (_computer is null) return;
        try
        {
            _computer.Accept(_visitor);
            float? temp = null, clock = null;

            foreach (IHardware hw in _computer.Hardware)
            {
                if (hw.HardwareType != HardwareType.Cpu) continue;
                foreach (ISensor s in hw.Sensors)
                {
                    if (s.Value is not float v) continue;

                    // Ryzen reports "Core (Tctl/Tdie)"; Intel reports
                    // "CPU Package". Prefer either, else the hottest core.
                    if (s.SensorType == SensorType.Temperature)
                    {
                        if (s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                            s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                            temp = v;
                        else if (temp is null && s.Name.StartsWith("Core", StringComparison.OrdinalIgnoreCase))
                            temp = Math.Max(temp ?? 0, v);
                    }
                    else if (s.SensorType == SensorType.Clock &&
                             s.Name.StartsWith("Core", StringComparison.OrdinalIgnoreCase))
                    {
                        clock = Math.Max(clock ?? 0, v);
                    }
                }
            }

            if (!_loggedSensors)
            {
                _loggedSensors = true;
                var names = new List<string>();
                foreach (IHardware hw in _computer.Hardware)
                    foreach (ISensor s in hw.Sensors)
                        if (s.SensorType == SensorType.Temperature)
                            names.Add($"{hw.HardwareType}/{s.Name}={(s.Value?.ToString() ?? "null")}");
                Log.Info("cpu temperature sensors: " + (names.Count == 0 ? "none reported" : string.Join("; ", names)));
            }

            if (temp is float t) { CpuTemp = t; CpuTempAvailable = true; }
            if (clock is float c) CpuClockMhz = (int)Math.Round(c);
        }
        catch { /* a transient sensor read failure should not kill the frame loop */ }
    }

    // ---- CPU load from kernel tick counters (locale- and privilege-free) ----
    private ulong _prevIdle, _prevKernel, _prevUser;

    private void UpdateLoad()
    {
        if (!GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user)) return;
        ulong i = ToUlong(idle), k = ToUlong(kernel), u = ToUlong(user);

        if (_prevKernel != 0 || _prevUser != 0)
        {
            ulong dIdle = i - _prevIdle, dKernel = k - _prevKernel, dUser = u - _prevUser;
            ulong total = dKernel + dUser;              // kernel already includes idle
            if (total > 0)
                CpuLoad = (float)Math.Clamp((total - dIdle) * 100.0 / total, 0, 100);
        }
        _prevIdle = i; _prevKernel = k; _prevUser = u;
    }

    private void UpdateMemory()
    {
        var st = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref st)) return;
        RamTotalGb = st.ullTotalPhys / 1024.0 / 1024.0 / 1024.0;
        RamUsedGb = (st.ullTotalPhys - st.ullAvailPhys) / 1024.0 / 1024.0 / 1024.0;
        RamPercent = (int)st.dwMemoryLoad;
    }

    private static ulong ToUlong(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength; public uint dwMemoryLoad;
        public ulong ullTotalPhys; public ulong ullAvailPhys;
        public ulong ullTotalPageFile; public ulong ullAvailPageFile;
        public ulong ullTotalVirtual; public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (IHardware sub in hardware.SubHardware) sub.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }

    public void Dispose()
    {
        try { _computer?.Close(); } catch { }
        _computer = null;
    }
}
