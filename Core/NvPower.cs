using System.Runtime.InteropServices;

namespace GhostDeck;

/// <summary>
/// Discrete-GPU board power in watts, from NVIDIA's own management library (nvml.dll, installed
/// with every NVIDIA driver - nothing of ours, no kernel driver). GpuTelemetry explains why
/// Windows alone cannot give watts: its power field is a share of the adapter's limit. NVML can.
///
/// The one rule: never be the reason the card wakes up. An NVML query can pull a sleeping
/// discrete GPU out of D3cold and keep it there, which on a hybrid laptop costs battery for
/// nothing. So NVML is asked only while GpuTelemetry already sees the card clocked (it reports no
/// clock at all once the card has powered down); otherwise the reading is -1 and the overlay shows
/// a dash, exactly like the GPU temperature. NVIDIA only - other vendors have no such library here.
/// </summary>
internal static class NvPower
{
    private delegate int InitFn();
    private delegate int HandleFn(uint index, out IntPtr device);
    private delegate int PowerFn(IntPtr device, out uint milliwatts);

    private static readonly object _lock = new();
    private static bool _tried;
    private static PowerFn? _power;
    private static IntPtr _device;
    private static DateTime _last = DateTime.MinValue;
    private static double _watts = -1;

    /// <summary>
    /// True on a machine whose discrete card is NVIDIA and that has nvml.dll - the overlay item is
    /// offered only then. Deliberately does not initialise NVML: that alone can wake the card.
    /// </summary>
    public static bool Available =>
        GpuTelemetry.GetRoles().Main is { Integrated: false } m && m.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
        && File.Exists(Path.Combine(Environment.SystemDirectory, "nvml.dll"));

    /// <summary>Board power in W, -1 = unavailable or the card is asleep. Throttled to ~1 s. Never throws.</summary>
    public static double Watts()
    {
        lock (_lock)
        {
            if ((DateTime.UtcNow - _last).TotalMilliseconds < 900) return _watts;
            _last = DateTime.UtcNow;
            _watts = -1;
            try
            {
                Load();
                if (_power == null) return _watts;
                // a powered-down card reports no clock: leave it asleep
                if (GpuTelemetry.Read() is not { Ok: true, Mhz: > 0 }) return _watts;
                if (_power(_device, out uint mw) == 0) _watts = mw / 1000.0;
            }
            catch { _power = null; }
            return _watts;
        }
    }

    private static void Load()
    {
        if (_tried) return;
        _tried = true;
        try
        {
            string[] paths =
            {
                Path.Combine(Environment.SystemDirectory, "nvml.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVSMI\nvml.dll"),
            };
            IntPtr lib = 0;
            foreach (var p in paths)
                if (File.Exists(p) && NativeLibrary.TryLoad(p, out lib)) break;
            if (lib == 0) return;
            var init = Marshal.GetDelegateForFunctionPointer<InitFn>(NativeLibrary.GetExport(lib, "nvmlInit_v2"));
            var handle = Marshal.GetDelegateForFunctionPointer<HandleFn>(NativeLibrary.GetExport(lib, "nvmlDeviceGetHandleByIndex_v2"));
            var power = Marshal.GetDelegateForFunctionPointer<PowerFn>(NativeLibrary.GetExport(lib, "nvmlDeviceGetPowerUsage"));
            // laptops carry one NVIDIA card, so index 0 is the discrete GPU the rest of the app means
            if (init() != 0 || handle(0, out _device) != 0) return;
            _power = power;
        }
        catch { _power = null; }
    }
}
