using Mirador.Core.Profiles;

namespace Mirador.Core.Tests.Profiles;

internal static class TestProfiles
{
    public static ProfileSet Two() => new(
        [
            new Profile("dreamair", "Dream Air", "pimax", BuildSource.Mirador, [], new Dictionary<string, string>()),
            new Profile("custom", "Custom", null, BuildSource.Mirador, [], new Dictionary<string, string>()),
        ],
        "dreamair");
}
