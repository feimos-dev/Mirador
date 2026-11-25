using System.Runtime.InteropServices;

namespace Mirador.Core.Gpu;

public enum GpuSchedulingState
{
    On,

    Off,

    NotOffered,

    Unknown,
}

public readonly record struct AdapterScheduling(bool Supported, bool Enabled);

public static class GpuScheduling
{
    public static GpuSchedulingState StateOf(IReadOnlyList<AdapterScheduling> adapters)
    {
        if (adapters.Count == 0)
        {
            return GpuSchedulingState.Unknown;
        }

        var offering = adapters.Where(a => a.Supported).ToList();
        return offering.Count == 0 ? GpuSchedulingState.NotOffered
            : offering.All(a => a.Enabled) ? GpuSchedulingState.On
            : GpuSchedulingState.Off;
    }

    public static string? Notice(GpuSchedulingState state) => state switch
    {
        GpuSchedulingState.Off => "Hardware-accelerated GPU scheduling is off. Monado as administrator needs it for the GPU's realtime priority, and Mirador is tested with it on. Turn it on in Windows' graphics settings, then restart Windows.",
        GpuSchedulingState.NotOffered => "This GPU's driver doesn't offer hardware-accelerated GPU scheduling, which Monado as administrator needs for the GPU's realtime priority. A newer driver may have it.",
        _ => null,
    };

    public static GpuSchedulingState Now()
    {
        try
        {
            return StateOf(Query());
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return GpuSchedulingState.Unknown;
        }
    }

    public static IReadOnlyList<AdapterScheduling> Query()
    {
        var enumerate = new EnumAdapters2();
        if (D3DKMTEnumAdapters2(ref enumerate) != 0 || enumerate.NumAdapters == 0)
        {
            return [];
        }

        var infoSize = Marshal.SizeOf<AdapterInfo>();
        var buffer = Marshal.AllocHGlobal(infoSize * (int)enumerate.NumAdapters);
        try
        {
            enumerate.Adapters = buffer;
            if (D3DKMTEnumAdapters2(ref enumerate) != 0)
            {
                return [];
            }

            var answers = new List<AdapterScheduling>();
            for (var i = 0; i < enumerate.NumAdapters; i++)
            {
                var adapter = Marshal.PtrToStructure<AdapterInfo>(buffer + (i * infoSize));
                try
                {
                    if (Caps(adapter.Handle) is { } caps)
                    {
                        answers.Add(new AdapterScheduling(Supported: (caps & HwSchSupported) != 0, Enabled: (caps & HwSchEnabled) != 0));
                    }
                }
                finally
                {
                    var close = new CloseAdapter { Handle = adapter.Handle };
                    _ = D3DKMTCloseAdapter(ref close);
                }
            }

            return answers;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const int KmtqaiTypeWddm27Caps = 70;
    private const uint HwSchSupported = 1;
    private const uint HwSchEnabled = 2;

    private static uint? Caps(uint handle)
    {
        var caps = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(caps, 0);
            var query = new QueryAdapterInfo { Handle = handle, Type = KmtqaiTypeWddm27Caps, PrivateDriverData = caps, PrivateDriverDataSize = sizeof(uint) };
            return D3DKMTQueryAdapterInfo(ref query) == 0 ? (uint)Marshal.ReadInt32(caps) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(caps);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumAdapters2
    {
        public uint NumAdapters;
        public IntPtr Adapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterInfo
    {
        public uint Handle;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint NumOfSources;
        public int PrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryAdapterInfo
    {
        public uint Handle;
        public int Type;
        public IntPtr PrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CloseAdapter
    {
        public uint Handle;
    }

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTEnumAdapters2(ref EnumAdapters2 enumerate);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo query);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref CloseAdapter close);
}
