using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mirador.Core.Lighthouse;
using Mirador.Core.Modules;
using Mirador.Core.Monado;

namespace Mirador.App.ViewModels;

public sealed record ProfileItem(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record StatusItem(string Label, string Text, StatusLevel Level)
{
    public string Glyph => Level switch
    {
        StatusLevel.Ok => "",
        StatusLevel.Warning => "",
        StatusLevel.Error => "",
        _ => "",
    };

    public Brush Brush => Level switch
    {
        StatusLevel.Ok => Theme("SystemFillColorSuccessBrush"),
        StatusLevel.Warning => Theme("SystemFillColorCautionBrush"),
        StatusLevel.Error => Theme("SystemFillColorCriticalBrush"),
        _ => Theme("TextFillColorSecondaryBrush"),
    };

    public static StatusItem From(StatusLine line) => new(line.Label, line.Text, line.Level);

    internal static Brush Theme(string key) => (Brush)Application.Current.Resources[key];
}

public sealed record DeviceItem(string Kind, string Detail, string Glyph)
{
    public static DeviceItem From(ControllerInfo device)
    {
        var kind = device.Kind == TrackedKind.Controller ? "Controller" : "Tracker";
        if (device.Hand is { } hand)
        {
            kind += hand == Hand.Left ? " L" : " R";
        }

        var battery = device.Battery is { } percent
            ? string.Create(CultureInfo.InvariantCulture, $"{percent}%") + (device.Charging ? ", charging" : "")
            : null;
        var detail = string.Join(", ", new[] { device.Hand is null ? device.Serial : null, battery }.Where(s => !string.IsNullOrEmpty(s)));
        return new DeviceItem(kind, detail, device.Kind == TrackedKind.Controller ? "" : "");
    }
}

public sealed record NoticeItem(string Key, string Title, string Message, InfoBarSeverity Severity, bool IsClosable);
