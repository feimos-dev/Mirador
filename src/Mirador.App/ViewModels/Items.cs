using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mirador.Core.Lighthouse;
using Mirador.Core.Modules;
using Mirador.Core.Monado;

namespace Mirador.App.ViewModels;

/// <summary>A profile in a list: its id and its name.</summary>
public sealed record ProfileItem(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A status line on Home, with the icon and colour of its level.</summary>
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

/// <summary>A controller or tracker Monado found, as a tile on Home.</summary>
/// <param name="Kind">"Controller L", "Controller R", "Controller" or "Tracker".</param>
/// <param name="Detail">Its battery, else its serial when its hand isn't known.</param>
/// <param name="Glyph">The icon for its kind (Segoe Fluent Icons).</param>
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
        var detail = battery ?? (device.Hand is null ? device.Serial ?? "" : "");
        if (battery is not null && device.Hand is null && device.Serial is { } serial)
        {
            detail = $"{serial}, {battery}";
        }

        return new DeviceItem(kind, detail, device.Kind == TrackedKind.Controller ? "" : "");
    }
}

/// <summary>A message shown in an <see cref="InfoBar"/>: a check that failed, a warning, a notice.</summary>
/// <param name="IsClosable">
/// Whether it has a close button: only something that happened once. A condition that holds now (a check, a warning
/// while Monado runs) stays until it no longer holds.
/// </param>
public sealed record NoticeItem(string Key, string Title, string Message, InfoBarSeverity Severity, bool IsClosable);
