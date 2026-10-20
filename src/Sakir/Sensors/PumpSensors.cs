using System.Reflection;
using Sakir.Core;
using Sakir.Device;

namespace Sakir.Sensors;

/// <summary>
/// Liquid temperature and pump/fan speeds, read from the pump's own HID
/// interface — a different USB interface from the LCD.
///
/// The panel never reports these: MSI Center reads them here and *sends* them to
/// the display, which is why the LCD class only has `SendHost...` methods for
/// them.
///
/// Deliberately avoids MSI's own <c>MSI_7ZW0Led.Init_S</c>. That does more than
/// open the device and its full behaviour was not verified — liquidctl's notes
/// warn that initialising these coolers can rewrite fan curves, and silently
/// resetting someone's fan profile to read a temperature would be a bad trade.
/// Instead we open the HID handle ourselves and use only two members, both
/// confirmed read-only by disassembly:
///
///   MSI_7ZW0_FanControl.Init(IntPtr)  - stores the handle, writes nothing
///   MSI_7ZW0_FanControl.Get_CoolerStatus() - sends the query D0 31 and reads
///                                            the reply back
/// </summary>
public sealed class PumpSensors : IDisposable
{
    private const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Public |
                                    BindingFlags.Instance | BindingFlags.Static;

    private const ushort Vid = 0x0DB0;

    /// <summary>Pump PIDs, from MSI_7ZW0Led's own constants.</summary>
    private static readonly ushort[] Pids = { 0x6A04, 0x6A05, 0x6A10, 0x6A11, 0xCA00, 0xCA02 };

    private Type? _fanType;
    private Type? _fanDataType;
    private object? _fan;
    private IntPtr _device = IntPtr.Zero;

    public bool Available { get; private set; }

    /// <summary>Coolant temperature at the outlet, in Celsius.</summary>
    public int LiquidTemp { get; private set; }

    /// <summary>Inlet temperature. Reads 0 on the S280 - not wired up there.</summary>
    public int InletTemp { get; private set; }

    // Channel mapping confirmed against live values on an S280:
    //   fan1 699   radiator fan
    //   fan2 0     fan3 0   unused headers
    //   fan4 1185  VRM fan on the pump head
    //   fan5 2857  pump
    public int PumpRpm { get; private set; }
    public int RadiatorRpm { get; private set; }
    public int VrmFanRpm { get; private set; }

    /// <summary>
    /// A pump that has stopped is the failure that matters here: it is silent,
    /// and nothing else shows it until the coolant is already hot.
    /// </summary>
    public bool PumpStopped => Available && PumpRpm == 0;

    public bool Start()
    {
        try
        {
            CoreLiquidDevice.EnsureWorkingDirectory();
            Assembly asm = Assembly.LoadFrom(
                Path.Combine(CoreLiquidDevice.MsiRoot, "Coreliquid", "API_Coreliquid.dll"));

            Type hid = asm.GetType("MsiHid.HID_Basic", true)!;
            _fanType = asm.GetType("API_Coreliquid.MSI_7ZW0_FanControl", true)!;
            _fanDataType = asm.GetType("API_Coreliquid.MSI_7ZW0_FanControl+FanData", false)
                           ?? _fanType.GetNestedType("FanData", BF);

            MethodInfo? open = hid.GetMethod("openMyDevice_Overlapped", BF);
            MethodInfo? connected = hid.GetMethod("IsDeviceConnect", BF);
            if (open is null || _fanDataType is null) return false;

            foreach (ushort pid in Pids)
            {
                // MSI tries interface 0 first and falls back to 1; do the same.
                foreach (ushort mi in new ushort[] { 0, 1 })
                {
                    if (connected?.Invoke(null, new object?[] { Vid, pid, mi, (ushort)0 }) is int c && c <= 0)
                        continue;

                    var h = (IntPtr)open.Invoke(null, new object?[] { Vid, pid, mi, (ushort)0, (ushort)1 })!;
                    if (h == IntPtr.Zero) continue;

                    _fan = Activator.CreateInstance(_fanType!);
                    if (_fanType!.GetMethod("Init", BF)?.Invoke(_fan, new object?[] { h }) is true)
                    {
                        _device = h;
                        Update();
                        // a plausible reading is the real proof it is the right interface
                        if (LiquidTemp is > 0 and < 100)
                        {
                            Available = true;
                            // Log every channel once: MSI's own data string puts
                            // Fan5 first, which is the pump, but that is worth
                            // confirming against real numbers rather than
                            // trusting field order.
                            Log.Info($"pump sensors: PID 0x{pid:X4} MI {mi} | " +
                                     $"outlet {LiquidTemp} C, inlet {InletTemp} C | " +
                                     $"fan1 {GetFanSpeed("Fan1")} fan2 {GetFanSpeed("Fan2")} " +
                                     $"fan3 {GetFanSpeed("Fan3")} fan4 {GetFanSpeed("Fan4")} " +
                                     $"fan5 {GetFanSpeed("Fan5")}");
                            return true;
                        }
                    }

                    hid.GetMethod("CloseDevice", BF)?.Invoke(null, new object?[] { h });
                    _device = IntPtr.Zero;
                    _fan = null;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Throttled("pumpstart", "pump sensors unavailable", ex, 600);
        }
        return Available = false;
    }

    public void Update()
    {
        if (_fan is null || _fanType is null || _device == IntPtr.Zero) return;
        try
        {
            if (_fanType.GetMethod("Get_CoolerStatus", BF)?.Invoke(_fan, null) is not true) return;

            LiquidTemp = GetInt("Temperature_Outlet");
            InletTemp = GetInt("Temperature_Inlet");
            PumpRpm = GetFanSpeed("Fan5");
            RadiatorRpm = GetFanSpeed("Fan1");
            VrmFanRpm = GetFanSpeed("Fan4");
        }
        catch
        {
            // keep the last good values rather than blanking the readout
        }
    }

    private int GetInt(string field)
    {
        object? v = _fanType!.GetField(field, BF)?.GetValue(_fan);
        return v is int i ? i : 0;
    }

    private int GetFanSpeed(string fanField)
    {
        object? fan = _fanType!.GetField(fanField, BF)?.GetValue(_fan);
        if (fan is null) return 0;
        object? v = _fanDataType!.GetField("Speed", BF)?.GetValue(fan);
        return v is int i ? i : 0;
    }

    public void Dispose()
    {
        if (_device == IntPtr.Zero) return;
        try
        {
            Assembly asm = Assembly.LoadFrom(
                Path.Combine(CoreLiquidDevice.MsiRoot, "Coreliquid", "API_Coreliquid.dll"));
            asm.GetType("MsiHid.HID_Basic", false)?
               .GetMethod("CloseDevice", BF)?
               .Invoke(null, new object?[] { _device });
        }
        catch { }
        _device = IntPtr.Zero;
        _fan = null;
        Available = false;
    }
}
