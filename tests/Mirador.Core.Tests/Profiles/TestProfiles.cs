using Mirador.Core.Profiles;

namespace Mirador.Core.Tests.Profiles;

/// <summary>Profiles for tests that need more than the one Mirador ships.</summary>
internal static class TestProfiles
{
    /// <summary>A headset profile with the Pimax module (selected) and one without a module, both on the bundled Monado.</summary>
    public static ProfileSet Two() => new(
        [
            new Profile("dreamair", "Dream Air", "pimax", BuildSource.Mirador, [], new Dictionary<string, string>()),
            new Profile("custom", "Custom", null, BuildSource.Mirador, [], new Dictionary<string, string>()),
        ],
        "dreamair");
}
