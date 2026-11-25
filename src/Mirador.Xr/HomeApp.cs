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
    private const float StarsM = 240;

    private const int StarCount = 3000;

    private const int StarSeed = 7;

    private const float StarLowest = 0.03f;

    private const float StarQuadRadii = 3;

    private readonly Mesh dome = Mesh.GenerateSphere(SkyM * 2, 4);
    private readonly Material sky = new(Shader.FromFile("sky.hlsl")) { FaceCull = Cull.Front };
    private readonly Mesh stars = Stars();
    private readonly Material starlight = new(Shader.FromFile("stars.hlsl"))
    {
        Transparency = Transparency.Add,
        DepthWrite = false,
        FaceCull = Cull.None,
    };

    public HomeScene()
    {
        Input.HandVisible(Handed.Max, true);
        Interaction.DefaultInteractors = DefaultInteractors.None;
        Interaction.DefaultDraw = false;
        Renderer.SkyboxVisible = false;
        Renderer.SetClip(0.05f, 400);
        starlight.SetVector("core", new Vec4(1 / StarQuadRadii, 1.2f, 0, 0));
    }

    public void Draw()
    {
        var head = Input.Head.position;
        floor.Draw(grid, Matrix.T(MathF.Round(head.x), 0, MathF.Round(head.z)));
        dome.Draw(sky, Matrix.T(head));
        stars.Draw(starlight, Matrix.T(head));
    }

    private static Mesh Stars()
    {
        var random = new Random(StarSeed);
        var verts = new Vertex[StarCount * 4];
        var inds = new uint[StarCount * 6];
        for (var i = 0; i < StarCount; i++)
        {
            var height = StarLowest + ((1 - StarLowest) * random.NextSingle());
            var turn = MathF.Tau * random.NextSingle();
            var flat = MathF.Sqrt(1 - (height * height));
            var dir = new Vec3(flat * MathF.Cos(turn), height, flat * MathF.Sin(turn));
            var across = Vec3.Cross(dir, Vec3.Up);
            var right = across.LengthSq > 1e-8f ? across.Normalized : Vec3.Right;
            var up = Vec3.Cross(right, dir);

            var bright = MathF.Pow(random.NextSingle(), 7);
            var warm = MathF.Pow(random.NextSingle(), 2);
            var rise = Math.Clamp((height - StarLowest) / 0.3f, 0, 1);
            var light = (0.26f + (0.74f * bright)) * rise * rise * (3 - (2 * rise));
            var colour = new Color32(
                (byte)(255 * (0.78f + (0.22f * warm))),
                (byte)(255 * (0.86f + (0.04f * warm))),
                (byte)(255 * (1 - (0.28f * warm))),
                (byte)(255 * light));

            var degrees = 0.035f + (0.05f * bright);
            var half = StarsM * MathF.Tan(degrees * MathF.PI / 180) * StarQuadRadii;
            var centre = dir * StarsM;
            for (var corner = 0; corner < 4; corner++)
            {
                var x = corner is 1 or 2 ? 1f : -1f;
                var y = corner >= 2 ? 1f : -1f;
                verts[(i * 4) + corner] = new Vertex(centre + (right * (x * half)) + (up * (y * half)), -dir, new Vec2(x, y), colour);
            }

            var first = (uint)(i * 4);
            inds[i * 6] = first;
            inds[(i * 6) + 1] = first + 1;
            inds[(i * 6) + 2] = first + 2;
            inds[(i * 6) + 3] = first;
            inds[(i * 6) + 4] = first + 2;
            inds[(i * 6) + 5] = first + 3;
        }

        var mesh = new Mesh();
        mesh.SetData(verts, inds);
        return mesh;
    }
}
