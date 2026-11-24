using Mirador.Ipc;
using StereoKit;

namespace Mirador.Xr;

/// <summary>
/// The home: a floor grid reaching to the horizon under a plain sky, at the stage's floor. It is a scene app like a
/// game, so Monado shows it whenever no game runs; under a game it draws nothing. It tells Mirador when it is the app
/// shown, which is what opens the dashboard.
/// </summary>
internal static class HomeApp
{
    public static int Run(Link link)
    {
        if (!SK.Initialize(Program.Settings(XrLink.HomeAppName)))
        {
            return 1;
        }

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

/// <summary>What the home draws: the floor grid at the stage's floor and the sky around the head, and nothing else.</summary>
internal sealed class HomeScene
{
    /// <summary>The floor's edge length; it has faded into the horizon long before its edge.</summary>
    private const float FloorM = 600;

    /// <summary>The sky's radius, inside the far clip plane.</summary>
    private const float SkyM = 250;

    private readonly Mesh floor = Mesh.GeneratePlane(new Vec2(FloorM, FloorM), Vec3.Up, Vec3.Forward);
    private readonly Material grid = new(Shader.FromFile("grid.hlsl"));
    private readonly Mesh dome = Mesh.GenerateSphere(SkyM * 2, 4);
    private readonly Material sky = new(Shader.FromFile("sky.hlsl")) { FaceCull = Cull.Front };

    public HomeScene()
    {
        // No hands, no controllers, no rays, and no skybox behind the scene's own sky.
        Input.HandVisible(Handed.Max, false);
        Interaction.DefaultInteractors = DefaultInteractors.None;
        Interaction.DefaultDraw = false;
        Renderer.SkyboxVisible = false;
        Renderer.SetClip(0.05f, 400);
    }

    public void Draw()
    {
        // The floor follows the head along the ground in whole metres, so its edge is never reached and its lines stay put.
        var head = Input.Head.position;
        floor.Draw(grid, Matrix.T(MathF.Round(head.x), 0, MathF.Round(head.z)));
        dome.Draw(sky, Matrix.T(head));
    }
}
