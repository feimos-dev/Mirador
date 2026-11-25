namespace Mirador.Helper.Tests;

public sealed class ElevatedFactAttribute : FactAttribute
{
    public ElevatedFactAttribute()
    {
        Skip = Elevated.SkipReason;
    }
}

public sealed class ElevatedTheoryAttribute : TheoryAttribute
{
    public ElevatedTheoryAttribute()
    {
        Skip = Elevated.SkipReason;
    }
}

internal static class Elevated
{
    public static string? SkipReason => Environment.IsPrivilegedProcess
        ? null
        : "The helper's pipe lets only SYSTEM and Administrators open more instances: run this test elevated.";
}
