using System.Reflection;
using Sakir.Device;

namespace Sakir.Sensors;

/// <summary>
/// GPU readings via the vendor DLLs MSI already ships in its Coreliquid folder
/// (ADLX.dll for AMD, NDA.dll for nVidia). API_Coreliquid.dll declares the
/// P/Invokes and the sensor structs for both, so we drive its DLL_ADLX /
/// DLL_NDA classes by reflection instead of writing our own bindings - which
/// also means no extra native dependency to ship.
/// </summary>
public sealed class GpuSensors
{
    private const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Public |
                                    BindingFlags.Instance | BindingFlags.Static;

    private Type? _api;      // DLL_ADLX or DLL_NDA
    private Type? _struct;   // ADLX_Sensors or NDA_Sensors
    private bool _isAmd;

    public bool Available { get; private set; }

    /// <summary>False on nVidia: MSI's API exposes neither hotspot nor power.</summary>
    public bool HasHotspotAndPower => _isAmd;
    public string Vendor => _isAmd ? "AMD" : "nVidia";

    public int Temp { get; private set; }
    public int Hotspot { get; private set; }
    public int Usage { get; private set; }
    public int ClockMhz { get; private set; }
    public int FanRpm { get; private set; }
    public int Watts { get; private set; }
    public int MemUsedMb { get; private set; }

    public bool Start()
    {
        // ADLX.dll / NDA.dll are also referenced by relative path, so the
        // working directory must already point at MSI Center.
        CoreLiquidDevice.EnsureWorkingDirectory();

        Assembly asm = Assembly.LoadFrom(
            Path.Combine(CoreLiquidDevice.MsiRoot, "Coreliquid", "API_Coreliquid.dll"));
        string coreliquid = Path.Combine(CoreLiquidDevice.MsiRoot, "Coreliquid");

        // AMD first, then nVidia; whichever initialises is the live one.
        if (TryInit(asm, "API_Coreliquid.DLL_ADLX", "API_Coreliquid.ADLX_Sensors",
                    "ADLX_Initialize", new object?[] { coreliquid }))
        {
            _isAmd = true;
            return Available = true;
        }
        if (TryInit(asm, "API_Coreliquid.DLL_NDA", "API_Coreliquid.NDA_Sensors",
                    "NDA_Initialize", Array.Empty<object?>()))
        {
            _isAmd = false;
            return Available = true;
        }
        return Available = false;
    }

    private bool TryInit(Assembly asm, string apiType, string structType, string initName, object?[] initArgs)
    {
        try
        {
            Type? api = asm.GetType(apiType);
            Type? st = asm.GetType(structType);
            if (api is null || st is null) return false;
            if (api.GetMethod(initName, BF)?.Invoke(null, initArgs) is not true) return false;
            _api = api; _struct = st;
            return true;
        }
        catch { return false; }
    }

    public void Update()
    {
        if (!Available || _api is null || _struct is null) return;
        try
        {
            object sensors = Activator.CreateInstance(_struct)!;
            var args = new object?[] { 0, sensors };
            string m = _isAmd ? "ADLX_GetSensors" : "NDA_GetSensors";
            if (_api.GetMethod(m, BF)?.Invoke(null, args) is not true) return;

            object s = args[1]!;
            if (_isAmd)
            {
                Temp = GetInt(s, "GPU_Temperature");
                Hotspot = GetInt(s, "GPU_Hotspot_Temperature");
                Usage = GetInt(s, "GPU_Usage");
                ClockMhz = GetInt(s, "GPU_Clock");
                FanRpm = GetInt(s, "GPU_FanSpeed_RPM");
                Watts = GetInt(s, "GPU_PowerConsumption_Watt");
                MemUsedMb = GetInt(s, "Memory_Used");
            }
            else
            {
                // MSI's NDA_Sensors uses different field names from the AMD
                // struct, and has no hotspot or watt reading at all. Reading the
                // AMD names here silently returned 0 (a fake 0 C). Names taken
                // from reflection over NDA_Sensors; untested on real nVidia
                // hardware.
                Temp = GetInt(s, "GPU_Temperature_Current");
                Usage = GetInt(s, "GPU_Usage");
                ClockMhz = GetInt(s, "GPU_Clock_Current");
                MemUsedMb = GetInt(s, "VRAM_Usage");
                Hotspot = 0;
                Watts = 0;
                FanRpm = 0;
                // no temperature field means the struct layout is not what we
                // expect - report "unavailable" instead of showing zeros
                if (_struct.GetField("GPU_Temperature_Current", BF) is null) Available = false;
            }
        }
        catch { /* keep the last good values rather than stalling the loop */ }
    }

    private int GetInt(object obj, string field)
    {
        object? v = _struct!.GetField(field, BF)?.GetValue(obj);
        return v switch { int i => i, null => 0, _ => Convert.ToInt32(v) };
    }
}
