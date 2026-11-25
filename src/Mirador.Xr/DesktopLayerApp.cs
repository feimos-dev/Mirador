using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Mirador.Core.Xr;
using Mirador.Ipc;
using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.KHR;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using XrAction = Silk.NET.OpenXR.Action;

namespace Mirador.Xr;

internal sealed unsafe class DesktopLayerApp : IDisposable
{
    private static readonly long[] Formats = [(long)Format.B8G8R8A8_UNorm_SRgb, (long)Format.B8G8R8A8_UNorm];

    private readonly XR xr = XR.GetApi();
    private readonly ConcurrentQueue<string> lines = new();
    private Instance instance;
    private ulong system;
    private Session session;
    private Space stage;
    private ID3D11Device? device;
    private ID3D11DeviceContext? context;

    private Swapchain swapchain;
    private long format;
    private (int Width, int Height) swapchainSize;
    private ID3D11Texture2D[] images = [];
    private bool pictured;

    private DesktopPlacement? placement;
    private DesktopCapture? capture;

    private ActionSet scrollSet;
    private XrAction padAction;
    private XrAction touchAction;
    private ulong leftHand;
    private ulong rightHand;
    private bool? pointing;
    private readonly PadScroll padScroll = new();
    private TimeSpan framePeriod = TimeSpan.FromSeconds(1 / 90.0);
    private volatile bool inputEnded;
    private DateTime? exitAsked;
    private bool running;
    private bool exiting;

    private static readonly TimeSpan ExitLimit = TimeSpan.FromSeconds(2);

    public static int Run(TextReader input)
    {
        DesktopLayerApp? app = null;
        try
        {
            app = new DesktopLayerApp();
            app.Start();
        }
        catch (Exception e)
        {
            Console.WriteLine($"The desktop can't be shown: {e.Message}");
            app?.Dispose();
            return 1;
        }

        using var running = app;
        var reader = new Thread(() => app.Read(input)) { IsBackground = true, Name = "dashboard lines" };
        reader.Start();
        try
        {
            app.Loop();
        }
        catch (Exception e)
        {
            Console.WriteLine($"The desktop stopped: {e.Message}");
            return 1;
        }

        return 0;
    }

    private void Read(TextReader input)
    {
        while (input.ReadLine() is { } line)
        {
            lines.Enqueue(line);
        }

        inputEnded = true;
    }

    private void Start()
    {
        var extensions = new[] { "XR_KHR_D3D11_enable", "XR_EXTX_overlay" };
        var names = new byte*[extensions.Length];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = (byte*)Marshal.StringToCoTaskMemUTF8(extensions[i]);
        }

        try
        {
            fixed (byte** namesPointer = names)
            {
                var info = new InstanceCreateInfo(StructureType.InstanceCreateInfo)
                {
                    EnabledExtensionCount = (uint)names.Length,
                    EnabledExtensionNames = namesPointer,
                };
                info.ApplicationInfo.ApiVersion = 1ul << 48;
                info.ApplicationInfo.ApplicationVersion = 1;
                var appName = System.Text.Encoding.UTF8.GetBytes(DesktopLayerLink.AppName);
                for (var i = 0; i < appName.Length; i++)
                {
                    info.ApplicationInfo.ApplicationName[i] = appName[i];
                }

                Check(xr.CreateInstance(&info, ref instance), "xrCreateInstance");
            }
        }
        finally
        {
            foreach (var name in names)
            {
                Marshal.FreeCoTaskMem((IntPtr)name);
            }
        }

        var systemInfo = new SystemGetInfo(StructureType.SystemGetInfo) { FormFactor = FormFactor.HeadMountedDisplay };
        Check(xr.GetSystem(instance, &systemInfo, ref system), "xrGetSystem");

        if (!xr.TryGetInstanceExtension<KhrD3D11Enable>(null, instance, out var d3d11))
        {
            throw new InvalidOperationException("this OpenXR runtime has no D3D11 binding");
        }

        var requirements = new GraphicsRequirementsD3D11KHR(StructureType.GraphicsRequirementsD3D11Khr);
        Check(d3d11.GetD3D11GraphicsRequirements(instance, system, &requirements), "xrGetD3D11GraphicsRequirementsKHR");
        CreateDevice(requirements.AdapterLuid);

        var binding = new GraphicsBindingD3D11KHR(StructureType.GraphicsBindingD3D11Khr) { Device = (void*)device!.NativePointer };
        var overlay = new SessionCreateInfoOverlayEXTX(StructureType.SessionCreateInfoOverlayExtx)
        {
            SessionLayersPlacement = uint.MaxValue - 1,
        };
        binding.Next = &overlay;
        var sessionInfo = new SessionCreateInfo(StructureType.SessionCreateInfo) { SystemId = system, Next = &binding };
        Check(xr.CreateSession(instance, &sessionInfo, ref session), "xrCreateSession");

        var spaceInfo = new ReferenceSpaceCreateInfo(StructureType.ReferenceSpaceCreateInfo)
        {
            ReferenceSpaceType = ReferenceSpaceType.Stage,
            PoseInReferenceSpace = new Posef(new Quaternionf(0, 0, 0, 1), new Vector3f(0, 0, 0)),
        };
        Check(xr.CreateReferenceSpace(session, &spaceInfo, ref stage), "xrCreateReferenceSpace");

        try
        {
            CreateScrollActions();
        }
        catch (Exception e)
        {
            Console.WriteLine($"The trackpads won't scroll: {e.Message}");
            scrollSet = default;
        }
    }

    private void CreateDevice(ulong luid)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
        using var adapter = factory.EnumAdapterByLuid<IDXGIAdapter>(*(Vortice.Luid*)&luid);
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels, out ID3D11Device created, out ID3D11DeviceContext immediate).CheckError();
        device = created;
        context = immediate;

        using var multithread = created.QueryInterface<ID3D11Multithread>();
        multithread.SetMultithreadProtected(true);
    }

    private void Loop()
    {
        while (!exiting)
        {
            PollEvents();
            if (inputEnded && exitAsked is null)
            {
                exitAsked = DateTime.UtcNow;
                if (!running)
                {
                    break;
                }

                xr.RequestExitSession(session);
            }

            if (DateTime.UtcNow - exitAsked > ExitLimit)
            {
                break;
            }

            if (!running)
            {
                Thread.Sleep(10);
                continue;
            }

            Frame();
        }
    }

    private void PollEvents()
    {
        while (true)
        {
            var buffer = new EventDataBuffer(StructureType.EventDataBuffer);
            if (xr.PollEvent(instance, &buffer) != Result.Success)
            {
                return;
            }

            if (buffer.Type == StructureType.EventDataInstanceLossPending)
            {
                exiting = true;
            }
            else if (buffer.Type == StructureType.EventDataSessionStateChanged)
            {
                var changed = (EventDataSessionStateChanged*)&buffer;
                Console.WriteLine($"Session {changed->State}.");
                switch (changed->State)
                {
                    case SessionState.Ready:
                        var begin = new SessionBeginInfo(StructureType.SessionBeginInfo) { PrimaryViewConfigurationType = ViewConfigurationType.PrimaryStereo };
                        Check(xr.BeginSession(session, &begin), "xrBeginSession");
                        running = true;
                        break;
                    case SessionState.Stopping:
                        xr.EndSession(session);
                        running = false;
                        break;
                    case SessionState.Exiting:
                    case SessionState.LossPending:
                        running = false;
                        exiting = true;
                        break;
                }
            }
        }
    }

    private void Take()
    {
        while (lines.TryDequeue(out var line))
        {
            if (line == DesktopLayerLink.Hide)
            {
                Place(null);
                pointing = null;
            }
            else if (line.StartsWith(DesktopLayerLink.Point + " ", StringComparison.Ordinal))
            {
                var hand = DesktopLayerLink.ParsePoint(line);
                if (hand != pointing)
                {
                    padScroll.Update(false, 0);
                    pointing = hand;
                }
            }
            else if (DesktopLayerLink.ParseShow(line) is { } next)
            {
                Place(next);
            }
        }
    }

    private void CreateScrollActions()
    {
        leftHand = PathOf("/user/hand/left");
        rightHand = PathOf("/user/hand/right");

        var setInfo = new ActionSetCreateInfo(StructureType.ActionSetCreateInfo);
        Name(setInfo.ActionSetName, "desktop");
        Name(setInfo.LocalizedActionSetName, "Desktop");
        var set = default(ActionSet);
        Check(xr.CreateActionSet(instance, &setInfo, &set), "xrCreateActionSet");
        scrollSet = set;

        padAction = CreateAction("scroll_pad", "Trackpad", ActionType.Vector2fInput);
        touchAction = CreateAction("scroll_touch", "Trackpad touch", ActionType.BooleanInput);

        var bindings = stackalloc ActionSuggestedBinding[]
        {
            new() { Action = padAction, Binding = PathOf("/user/hand/left/input/trackpad") },
            new() { Action = padAction, Binding = PathOf("/user/hand/right/input/trackpad") },
            new() { Action = touchAction, Binding = PathOf("/user/hand/left/input/trackpad/touch") },
            new() { Action = touchAction, Binding = PathOf("/user/hand/right/input/trackpad/touch") },
        };
        var suggested = new InteractionProfileSuggestedBinding(StructureType.InteractionProfileSuggestedBinding)
        {
            InteractionProfile = PathOf("/interaction_profiles/valve/index_controller"),
            CountSuggestedBindings = 4,
            SuggestedBindings = bindings,
        };
        Check(xr.SuggestInteractionProfileBinding(instance, &suggested), "xrSuggestInteractionProfileBindings");

        var attach = new SessionActionSetsAttachInfo(StructureType.SessionActionSetsAttachInfo) { CountActionSets = 1, ActionSets = &set };
        Check(xr.AttachSessionActionSets(session, &attach), "xrAttachSessionActionSets");
    }

    private XrAction CreateAction(string name, string localized, ActionType type)
    {
        var hands = stackalloc ulong[] { leftHand, rightHand };
        var info = new ActionCreateInfo(StructureType.ActionCreateInfo) { ActionType = type, CountSubactionPaths = 2, SubactionPaths = hands };
        Name(info.ActionName, name);
        Name(info.LocalizedActionName, localized);
        var action = default(XrAction);
        Check(xr.CreateAction(scrollSet, &info, &action), "xrCreateAction");
        return action;
    }

    private void ScrollWithPad()
    {
        if (scrollSet.Handle == 0 || placement is null || pointing is not { } left)
        {
            padScroll.Update(false, 0);
            return;
        }

        var active = new ActiveActionSet { ActionSet = scrollSet };
        var sync = new ActionsSyncInfo(StructureType.ActionsSyncInfo) { CountActiveActionSets = 1, ActiveActionSets = &active };
        if (xr.SyncAction(session, &sync) < 0)
        {
            return;
        }

        var get = new ActionStateGetInfo(StructureType.ActionStateGetInfo) { Action = touchAction, SubactionPath = left ? leftHand : rightHand };
        var touch = new ActionStateBoolean(StructureType.ActionStateBoolean);
        var pad = new ActionStateVector2f(StructureType.ActionStateVector2f);
        var touched = xr.GetActionStateBoolean(session, &get, &touch) >= 0 && touch.IsActive != 0 && touch.CurrentState != 0;
        get.Action = padAction;
        touched &= xr.GetActionStateVector2(session, &get, &pad) >= 0 && pad.IsActive != 0;
        DesktopMouse.Turn(padScroll.Update(touched, pad.CurrentState.Y));
    }

    private ulong PathOf(string path)
    {
        var text = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            ulong value = 0;
            Check(xr.StringToPath(instance, (byte*)text, &value), "xrStringToPath");
            return value;
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    private static void Name(byte* buffer, string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(name);
        for (var i = 0; i < bytes.Length; i++)
        {
            buffer[i] = bytes[i];
        }

        buffer[bytes.Length] = 0;
    }

    private void Place(DesktopPlacement? next)
    {
        if (next?.Monitor != placement?.Monitor)
        {
            capture?.Dispose();
            capture = null;
            pictured = false;
            if (next is not null)
            {
                try
                {
                    capture = new DesktopCapture((IntPtr)next.Monitor, device!, framePeriod);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Monitor {next.Monitor} can't be captured: {e.Message}");
                    exiting = true;
                    next = null;
                }
            }
        }

        placement = next;
    }

    private void Frame()
    {
        var waitInfo = new FrameWaitInfo(StructureType.FrameWaitInfo);
        var state = new FrameState(StructureType.FrameState);
        if (xr.WaitFrame(session, &waitInfo, &state) != Result.Success)
        {
            Thread.Sleep(10);
            return;
        }

        if (state.PredictedDisplayPeriod > 0)
        {
            framePeriod = TimeSpan.FromTicks(state.PredictedDisplayPeriod / 100);
        }

        var beginInfo = new FrameBeginInfo(StructureType.FrameBeginInfo);
        xr.BeginFrame(session, &beginInfo);

        Take();
        capture?.TakeNewest(CopyIn);
        ScrollWithPad();

        var quad = new CompositionLayerQuad(StructureType.CompositionLayerQuad);
        var layer = (CompositionLayerBaseHeader*)&quad;
        var show = placement is not null && pictured && state.ShouldRender != 0;
        if (show)
        {
            var p = placement!.Pose;
            quad.Space = stage;
            quad.EyeVisibility = EyeVisibility.Both;
            quad.SubImage = new SwapchainSubImage
            {
                Swapchain = swapchain,
                ImageRect = new Rect2Di(new Offset2Di(0, 0), new Extent2Di(swapchainSize.Width, swapchainSize.Height)),
            };
            quad.Pose = new Posef(new Quaternionf(p[0], p[1], p[2], p[3]), new Vector3f(p[4], p[5], p[6]));
            quad.Size = new Extent2Df(placement.Width, placement.Height);
        }

        var endInfo = new FrameEndInfo(StructureType.FrameEndInfo)
        {
            DisplayTime = state.PredictedDisplayTime,
            EnvironmentBlendMode = EnvironmentBlendMode.Opaque,
            LayerCount = show ? 1u : 0u,
            Layers = show ? &layer : null,
        };
        xr.EndFrame(session, &endInfo);
    }

    private void CopyIn(ID3D11Texture2D picture, int width, int height)
    {
        if (swapchain.Handle == 0 || swapchainSize != (width, height))
        {
            MakeSwapchain(width, height);
        }

        uint index = 0;
        var acquire = new SwapchainImageAcquireInfo(StructureType.SwapchainImageAcquireInfo);
        if (xr.AcquireSwapchainImage(swapchain, &acquire, &index) != Result.Success)
        {
            return;
        }

        var wait = new SwapchainImageWaitInfo(StructureType.SwapchainImageWaitInfo) { Timeout = long.MaxValue };
        if (xr.WaitSwapchainImage(swapchain, &wait) != Result.Success)
        {
            return;
        }

        context!.CopySubresourceRegion(images[index], 0, 0, 0, 0, picture, 0, new Vortice.Mathematics.Box(0, 0, 0, width, height, 1));
        var release = new SwapchainImageReleaseInfo(StructureType.SwapchainImageReleaseInfo);
        xr.ReleaseSwapchainImage(swapchain, &release);
        pictured = true;
    }

    private void MakeSwapchain(int width, int height)
    {
        DestroySwapchain();
        if (format == 0)
        {
            uint count = 0;
            Check(xr.EnumerateSwapchainFormats(session, 0, &count, null), "xrEnumerateSwapchainFormats");
            var offered = new long[count];
            fixed (long* formats = offered)
            {
                Check(xr.EnumerateSwapchainFormats(session, count, &count, formats), "xrEnumerateSwapchainFormats");
            }

            format = Formats.FirstOrDefault(offered.Contains);
            if (format == 0)
            {
                throw new InvalidOperationException("the runtime offers no BGRA swapchain format");
            }
        }

        var info = new SwapchainCreateInfo(StructureType.SwapchainCreateInfo)
        {
            UsageFlags = SwapchainUsageFlags.TransferDstBit | SwapchainUsageFlags.SampledBit | SwapchainUsageFlags.ColorAttachmentBit,
            Format = format,
            SampleCount = 1,
            Width = (uint)width,
            Height = (uint)height,
            FaceCount = 1,
            ArraySize = 1,
            MipCount = 1,
        };
        Check(xr.CreateSwapchain(session, &info, ref swapchain), "xrCreateSwapchain");

        uint imageCount = 0;
        Check(xr.EnumerateSwapchainImages(swapchain, 0, &imageCount, null), "xrEnumerateSwapchainImages");
        var d3dImages = new SwapchainImageD3D11KHR[imageCount];
        for (var i = 0; i < d3dImages.Length; i++)
        {
            d3dImages[i] = new SwapchainImageD3D11KHR(StructureType.SwapchainImageD3D11Khr);
        }

        fixed (SwapchainImageD3D11KHR* first = d3dImages)
        {
            Check(xr.EnumerateSwapchainImages(swapchain, imageCount, &imageCount, (SwapchainImageBaseHeader*)first), "xrEnumerateSwapchainImages");
        }

        images = d3dImages.Select(image =>
        {
            Marshal.AddRef((IntPtr)image.Texture);
            return new ID3D11Texture2D((IntPtr)image.Texture);
        }).ToArray();
        swapchainSize = (width, height);
        pictured = false;
        Console.WriteLine($"Showing the monitor at {width}x{height} (swapchain format {format}, {images.Length} images).");
    }

    private void DestroySwapchain()
    {
        foreach (var image in images)
        {
            image.Dispose();
        }

        images = [];
        if (swapchain.Handle != 0)
        {
            xr.DestroySwapchain(swapchain);
            swapchain = default;
        }
    }

    public void Dispose()
    {
        capture?.Dispose();
        DestroySwapchain();
        if (stage.Handle != 0)
        {
            xr.DestroySpace(stage);
        }

        if (session.Handle != 0)
        {
            xr.DestroySession(session);
        }

        if (instance.Handle != 0)
        {
            xr.DestroyInstance(instance);
        }

        context?.Dispose();
        device?.Dispose();
        xr.Dispose();
    }

    private static void Check(Result result, string call)
    {
        if (result < 0)
        {
            throw new InvalidOperationException($"{call} failed ({result})");
        }
    }
}
