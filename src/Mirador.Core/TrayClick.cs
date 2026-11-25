namespace Mirador.Core;

public static class TrayClick
{
    public static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(500);

    public static bool ShouldHide(bool visible, bool minimized, bool foreground, TimeSpan sinceDeactivated) =>
        visible && !minimized && (foreground || sinceDeactivated < Grace);
}
