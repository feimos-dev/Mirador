using Mirador.Helper.DirectMode;
using Mirador.Ipc;

namespace Mirador.Helper.Tests;

public sealed class LayerRegistrationTests : IDisposable
{
    private readonly TempDir dir = new();
    private readonly FakeImplicitLayers layers = new();
    private readonly LayerRegistration registration;

    public LayerRegistrationTests()
    {
        dir.Touch("Mirador", "layer", "mirador_direct_mode.json");
        registration = new LayerRegistration(layers, Path.Combine(dir.Path, "Mirador"));
    }

    public void Dispose() => dir.Dispose();

    private string Manifest => Path.Combine(dir.Path, "Mirador", "layer", "mirador_direct_mode.json");

    [Fact]
    public void Register_adds_the_layer_that_comes_with_mirador_and_again_is_fine()
    {
        Assert.True(registration.Register("nvidia").Ok);
        Assert.True(registration.Register("nvidia").Ok);

        Assert.Equal([Manifest], layers.Values);
    }

    [Fact]
    public void Unregister_takes_out_only_miradors_layer()
    {
        layers.Values.Add(@"C:\Other\layer.json");
        registration.Register("nvidia");

        Assert.True(registration.Unregister("nvidia").Ok);
        Assert.True(registration.Unregister("nvidia").Ok);

        Assert.Equal([@"C:\Other\layer.json"], layers.Values);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("amd")]
    [InlineData(@"C:\Users\x\evil.json")]
    public void An_unknown_kind_changes_nothing(string? kind)
    {
        Assert.False(registration.Register(kind).Ok);
        Assert.False(registration.Unregister(kind).Ok);
        Assert.Empty(layers.Values);
    }

    [Fact]
    public void A_missing_layer_isnt_registered()
    {
        File.Delete(Manifest);

        var reply = registration.Register("nvidia");

        Assert.False(reply.Ok);
        Assert.StartsWith("The direct-mode layer that comes with Mirador is missing", reply.Error);
        Assert.Empty(layers.Values);
    }

    [Fact]
    public void A_registry_that_refuses_is_said()
    {
        layers.Failure = new UnauthorizedAccessException("Access is denied.");

        Assert.Equal(new HelperReply(false, "Windows wouldn't change the Vulkan layers (Access is denied)."), registration.Register("nvidia"));
    }

    [Fact]
    public async Task The_handler_passes_the_requests_kind()
    {
        var handler = new HelperHandler(new FakeServiceControl(), new FakeProcessControl(), new FakeRuntimeRegistry(), layers: registration);

        var reply = await handler.HandleAsync(new HelperRequest(HelperCommand.RegisterLayer, Layer: DirectModeEnvironment.Nvidia));

        Assert.True(reply.Ok);
        Assert.Equal([Manifest], layers.Values);
    }

    private sealed class FakeImplicitLayers : IImplicitLayers
    {
        public List<string> Values { get; } = [];

        public Exception? Failure { get; set; }

        public void Add(string manifest)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            if (!Values.Contains(manifest))
            {
                Values.Add(manifest);
            }
        }

        public void Remove(string manifest) => Values.Remove(manifest);
    }
}
