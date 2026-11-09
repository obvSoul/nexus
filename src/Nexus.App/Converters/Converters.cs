using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Nexus.App.Converters;

/// <summary>Visible when the bound string is non-empty.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Visible when the bound bool is true (used with TextBox.Text.IsEmpty for the placeholder).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
