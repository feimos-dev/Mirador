using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Mirador.App.ViewModels;

public sealed class PassThroughConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, string language) => value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, string language) => value;
}

public sealed class VisibleWhenTrueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, string language) =>
        value is Visibility.Visible;
}
