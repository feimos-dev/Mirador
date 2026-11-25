using Mirador.Ipc;
using StereoKit;

namespace Mirador.Xr;

internal static class HomeApp
{
    public static int Run(Link link)
    {
        if (!SK.Initialize(Program.Settings(XrLink.HomeAppName)))
        {
            return 1;
        }

        ControllerModels.Follow();
        var scene = new HomeScene();
        link.Send(XrLink.Running);
        bool? shown = null;
        SK.Run(() =>
        {
            while (Program.Next(link, out _, out _))
            {
            }

            var now = SK.AppFocus == AppFocus.Active;
            if (now != shown)
            {
                shown = now;
                link.Send(XrLink.Flag(XrLink.Shown, now));
            }

            if (SK.AppFocus == AppFocus.Hidden)
            {
                return;
            }

            scene.Draw();
        });
        return 0;
    }
}

internal sealed class HomeScene
{
    private const float FloorM = 600;

    private const float SkyM = 250;

    private readonly Mesh floor = Mesh.GeneratePlane(new Vec2(FloorM, FloorM), Vec3.Up, Vec3.Forward);
    private readonly Material grid = new(Shader.FromFile("grid.hlsl"));
    private readonly Mesh dome = Mesh.GenerateSphere(SkyM * 2, 4);
    private readonly Material sky = new(Shader.FromFile("sky.hlsl")) { FaceCull = Cull.Front };

    public HomeScene()
    {
        Input.HandVisible(Handed.Max, true);
        Interaction.DefaultInteractors = DefaultInteractors.None;
        Interaction.DefaultDraw = false;
        Renderer.SkyboxVisible = false;
        Renderer.SetClip(0.05f, 400);
    }

    public void Draw()
    {
        var head = Input.Head.position;
        floor.Draw(grid, Matrix.T(MathF.Round(head.x), 0, MathF.Round(head.z)));
        dome.Draw(sky, Matrix.T(head));
    }
}
