using Mirador.Ipc;

namespace Mirador.Core.Tests.Monado;

public class MiradorMonadoTests
{
    [Fact]
    public void Date_of_a_weekly_version() =>
        Assert.Equal(new DateOnly(2026, 11, 10), MiradorMonado.DateOf("monado-2026.11.10-1a2b3c4d5e"));

    [Theory]
    [InlineData("")]
    [InlineData("monado-2026.13.40-abc1234")]
    [InlineData("monado-2026.02.30-abc1234")]
    [InlineData("monado-2026.11.10")]
    [InlineData("foo")]
    public void Date_of_something_else_is_null(string version) => Assert.Null(MiradorMonado.DateOf(version));
}
