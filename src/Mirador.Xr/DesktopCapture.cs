using System.Runtime.InteropServices;
using Mirador.Core.Xr;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Mirador.Xr;

/// <summary>A monitor of the Windows desktop.</summary>
/// <param name="Handle">Windows' handle for it (an HMONITOR).</param>
/// <param name="Bounds">Where it is on the virtual desktop, in pixels.</param>
/// <param name="Primary">Whether it is the primary monitor.</param>
internal sealed record DesktopMonitor(IntPtr Handle, DesktopRect Bounds, bool Primary);

/// <summary>
/// One monitor's picture through Windows.Graphics.Capture, with the mouse pointer in it, on the desktop program's own
/// D3D11 device: each new frame stays on the GPU for the caller to copy. Windows only delivers a frame when the
/// monitor's picture changed, and no faster than the interval it is given where Windows can limit that.
/// </summary>
internal sealed class DesktopCapture : IDisposable
{
    private static readonly Guid CaptureItemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private readonly IDirect3DDevice winrtDevice;
    private readonly GraphicsCaptureItem item;
    private readonly Direct3D11CaptureFramePool pool;
    private readonly GraphicsCaptureSession session;

    private Windows.Graphics.SizeInt32 poolSize;

    /// <param name="interval">The shortest time between frames, such as the headset's frame period.</param>
    /// <exception cref="Exception">Windows can't capture this monitor (capture not supported, the monitor has gone).</exception>
    public DesktopCapture(IntPtr monitor, ID3D11Device device, TimeSpan interval)
    {
        using (var dxgi = device.QueryInterface<IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var inspectable));
            try
            {
                winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            }
            finally
            {
                Marshal.Release(inspectable);
            }
        }

        var itemPointer = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>().CreateForMonitor(monitor, CaptureItemId);
        try
        {
            item = GraphicsCaptureItem.FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }

        poolSize = item.Size;
        pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, poolSize);
        session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = true;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
        {
            // Windows 11: no yellow border around the monitor. Windows 10 keeps it.
            try
            {
                session.IsBorderRequired = false;
            }
            catch (Exception)
            {
            }
        }

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
        {
            // Windows 11 24H2: a fast monitor or a video on it is captured no faster than the headset shows it.
            try
            {
                session.MinUpdateInterval = interval;
            }
            catch (Exception)
            {
            }
        }

        session.StartCapture();
    }

    /// <summary>
    /// Gives the newest frame since the last call, if there is one, to <paramref name="use"/>: its texture and the size
    /// of its content, from the texture's top left. The texture goes back to Windows once <paramref name="use"/> returns.
    /// </summary>
    public bool TakeNewest(Action<ID3D11Texture2D, int, int> use)
    {
        Direct3D11CaptureFrame? frame = null;
        while (pool.TryGetNextFrame() is { } next)
        {
            frame?.Dispose();
            frame = next;
        }

        if (frame is null)
        {
            return false;
        }

        using (frame)
        {
            var size = frame.ContentSize;
            if (size.Width != poolSize.Width || size.Height != poolSize.Height)
            {
                // The monitor's mode changed: frames of the new size from the next one on.
                poolSize = size;
                pool.Recreate(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, poolSize);
                return false;
            }

            var id = typeof(ID3D11Texture2D).GUID;
            var pointer = frame.Surface.As<IDirect3DDxgiInterfaceAccess>().GetInterface(ref id);
            using var texture = new ID3D11Texture2D(pointer);
            use(texture, size.Width, size.Height);
            return true;
        }
    }

    public void Dispose()
    {
        session.Dispose();
        pool.Dispose();
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] in Guid iid);

        IntPtr CreateForMonitor([In] IntPtr monitor, [In] in Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }
}
