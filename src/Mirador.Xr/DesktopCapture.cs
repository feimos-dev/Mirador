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

/// <summary>Gives the pictures a <see cref="DesktopPanel"/> shows.</summary>
internal interface IDesktopFrames : IDisposable
{
    /// <summary>
    /// The newest picture since the last call, as BGRA rows from the top, when there is one: <paramref name="pixels"/>
    /// is made or made larger as needed and holds <paramref name="width"/> × <paramref name="height"/> × 4 bytes.
    /// </summary>
    bool TryRead(ref byte[] pixels, out int width, out int height);
}

/// <summary>
/// One monitor's picture through Windows.Graphics.Capture, with the mouse pointer in it. Each new frame is copied on
/// the GPU (halved first when it is wider than <see cref="DesktopMaths.FullWidth"/>) and read back into memory, which
/// is what the dashboard's renderer takes; Windows only delivers a frame when the monitor's picture changed.
/// </summary>
internal sealed unsafe class DesktopCapture : IDesktopFrames
{
    private static readonly Guid CaptureItemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly IDirect3DDevice winrtDevice;
    private readonly GraphicsCaptureItem item;
    private readonly Direct3D11CaptureFramePool pool;
    private readonly GraphicsCaptureSession session;

    private Windows.Graphics.SizeInt32 poolSize;
    private ID3D11Texture2D? shrink; // the frame with its smaller copies, when frames are halved
    private ID3D11ShaderResourceView? shrinkView;
    private ID3D11Texture2D? staging; // what is read back
    private (int Width, int Height) stagingSize;

    /// <exception cref="Exception">Windows can't capture this monitor (no GPU device, capture not supported, the monitor has gone).</exception>
    public DesktopCapture(DesktopMonitor monitor)
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out ID3D11Device created, out ID3D11DeviceContext immediate).CheckError();
        device = created;
        context = immediate;

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

        var itemPointer = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>().CreateForMonitor(monitor.Handle, CaptureItemId);
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

        session.StartCapture();
    }

    public bool TryRead(ref byte[] pixels, out int width, out int height)
    {
        (width, height) = (0, 0);

        // Only the newest frame counts.
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
            var (w, h, halved) = DesktopMaths.Readback(size.Width, size.Height);
            Prepare(size.Width, size.Height, w, h, halved);

            // Only the frame's content: after a mode change its texture can still be the larger one.
            var content = new Vortice.Mathematics.Box(0, 0, 0, size.Width, size.Height, 1);
            if (halved)
            {
                context.CopySubresourceRegion(shrink!, 0, 0, 0, 0, texture, 0, content);
                context.GenerateMips(shrinkView!);
                context.CopySubresourceRegion(staging!, 0, 0, 0, 0, shrink!, 1, null);
            }
            else
            {
                context.CopySubresourceRegion(staging!, 0, 0, 0, 0, texture, 0, content);
            }

            var mapped = context.Map(staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var rowBytes = w * 4;
                if (pixels.Length < rowBytes * h)
                {
                    pixels = new byte[rowBytes * h];
                }

                fixed (byte* to = pixels)
                {
                    for (var row = 0; row < h; row++)
                    {
                        Buffer.MemoryCopy((byte*)mapped.DataPointer + ((long)row * mapped.RowPitch), to + ((long)row * rowBytes), rowBytes, rowBytes);
                    }
                }
            }
            finally
            {
                context.Unmap(staging!, 0);
            }

            (width, height) = (w, h);
            return true;
        }
    }

    public void Dispose()
    {
        session.Dispose();
        pool.Dispose();
        shrinkView?.Dispose();
        shrink?.Dispose();
        staging?.Dispose();
        context.Dispose();
        device.Dispose();
    }

    /// <summary>The textures a frame of this size goes through, made again when the size changes.</summary>
    private void Prepare(int frameWidth, int frameHeight, int width, int height, bool halved)
    {
        if (staging is not null && stagingSize == (width, height) && (shrink is not null) == halved)
        {
            return;
        }

        shrinkView?.Dispose();
        shrink?.Dispose();
        staging?.Dispose();
        (shrinkView, shrink) = (null, null);

        if (halved)
        {
            // Two levels: the frame, and its half, which GenerateMips fills.
            shrink = device.CreateTexture2D(new Texture2DDescription(
                Format.B8G8R8A8_UNorm, (uint)frameWidth, (uint)frameHeight, 1, 2,
                BindFlags.ShaderResource | BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, 1, 0,
                ResourceOptionFlags.GenerateMips));
            shrinkView = device.CreateShaderResourceView(shrink);
        }

        staging = device.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        stagingSize = (width, height);
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
