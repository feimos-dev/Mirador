using Mirador.Ipc;

namespace Mirador.Helper.Tests;

[Trait("Category", "Hardware")]
public class InstalledHelperTests
{
    [Trait("Category", "Machine")]
    [MachineFact]
    public async Task The_installed_helper_answers_ping()
    {
        var reply = await new HelperClient().SendAsync(HelperCommand.Ping, CancellationToken.None);

        Assert.True(reply.Ok, reply.Error);
        Assert.False(string.IsNullOrWhiteSpace(reply.Value));
    }
}
