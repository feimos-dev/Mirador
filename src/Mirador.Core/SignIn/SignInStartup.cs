using System.Security;

namespace Mirador.Core.SignIn;

public sealed class SignInStartup(IRunKeys keys, string exePath)
{
    public const string ValueName = "Mirador";

    public const string Flag = "--sign-in";

    public const string RemoveFlag = "--remove-sign-in";

    public string Command => $"\"{exePath}\" {Flag}";

    public bool IsOn
    {
        get
        {
            try
            {
                if (!string.Equals(keys.GetRun(ValueName), Command, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return keys.GetApproved(ValueName) is not { Length: > 0 } approved || (approved[0] & 1) == 0;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
            {
                throw new MiradorException($"Mirador couldn't read whether it starts at sign-in ({Reason(e)}).");
            }
        }
    }

    public void TurnOn()
    {
        try
        {
            keys.SetRun(ValueName, Command);
            keys.DeleteApproved(ValueName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new MiradorException($"Mirador couldn't set itself to start at sign-in ({Reason(e)}). Try again.");
        }
    }

    public void TurnOff()
    {
        try
        {
            keys.DeleteRun(ValueName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new MiradorException($"Mirador couldn't stop itself from starting at sign-in ({Reason(e)}). Try again.");
        }
    }

    private static string Reason(Exception e) => e.Message.Trim().TrimEnd('.');
}
