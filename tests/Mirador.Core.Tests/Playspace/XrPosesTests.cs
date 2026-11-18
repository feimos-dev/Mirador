using Mirador.Core.Monado.Libmonado;
using Mirador.Core.Playspace;

namespace Mirador.Core.Tests.Playspace;

public class XrPosesTests
{
    private const string Both =
        """XR-POSES {"head":{"p":[0.120000,1.650000,-0.300000],"q":[0.000000,0.707107,0.000000,0.707107]},"left":{"p":[-0.2,0.06,-0.5],"q":[0,0,0,1]},"right":{"p":[0.2,0.9,-0.4],"q":[0,0,0,1]}}""";

    [Fact]
    public void Reads_the_head_and_both_hands()
    {
        var poses = XrPoses.Parse(["some log line", Both]);

        Assert.NotNull(poses);
        Assert.Equal(new MonadoPose(0, 0.707107f, 0, 0.707107f, 0.12f, 1.65f, -0.3f), poses.Head);
        Assert.Equal(new MonadoPose(0, 0, 0, 1, -0.2f, 0.06f, -0.5f), poses.Left);
        Assert.Equal(new MonadoPose(0, 0, 0, 1, 0.2f, 0.9f, -0.4f), poses.Right);
    }

    [Fact]
    public void An_untracked_hand_is_null()
    {
        var poses = XrPoses.Parse(["""XR-POSES {"head":{"p":[0,1.6,0],"q":[0,0,0,1]},"left":null,"right":{"p":[0,1,0],"q":[0,0,0,1]}}"""]);

        Assert.NotNull(poses);
        Assert.Null(poses.Left);
        Assert.NotNull(poses.Right);
    }

    [Theory]
    [InlineData("")]
    [InlineData("XR-POSES")]
    [InlineData("XR-POSES not json")]
    [InlineData("XR-POSES []")]
    [InlineData("""XR-POSES {"left":null,"right":null}""")]
    [InlineData("""XR-POSES {"head":null,"left":null,"right":null}""")]
    [InlineData("""XR-POSES {"head":{"p":[0,1],"q":[0,0,0,1]},"left":null,"right":null}""")]
    [InlineData("""XR-POSES {"head":{"p":[0,1,0],"q":[0,0,1]},"left":null,"right":null}""")]
    [InlineData("""XR-POSES {"head":{"p":[0,"1",0],"q":[0,0,0,1]},"left":null,"right":null}""")]
    [InlineData("""XR-POSES {"head":{"p":[0,1,0],"q":[0,0,0,0]},"left":null,"right":null}""")]
    [InlineData("""XR-POSES {"head":{"p":[0,1e39,0],"q":[0,0,0,1]},"left":null,"right":null}""")]
    [InlineData("""XR-POSES {"head":{"p":[0,1,0],"q":[0,0,0,1]},"left":7,"right":null}""")]
    [InlineData("""XR-CHECK {"head":{"p":[0,1,0],"q":[0,0,0,1]},"left":null,"right":null}""")]
    public void A_line_that_isnt_a_result_gives_none(string line)
    {
        Assert.Null(XrPoses.Parse([line]));
    }

    [Fact]
    public void The_failure_line_says_why()
    {
        Assert.Equal("no poses within 30 s (after 12 frames)", XrPoses.Failure(["log", "XR-POSES-FAIL no poses within 30 s (after 12 frames)"]));
        Assert.Null(XrPoses.Failure([Both]));
        Assert.Null(XrPoses.Parse(["XR-POSES-FAIL xrCreateInstance failed: XR_ERROR_RUNTIME_UNAVAILABLE"]));
    }
}
