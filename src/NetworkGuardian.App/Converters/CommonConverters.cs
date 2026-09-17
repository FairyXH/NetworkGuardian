using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace NetworkGuardian.App.Converters;

/// <summary>Converts a boolean into a <see cref="Visibility"/> value.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;

        // "Invert" parameter flips the result, which keeps the XAML free of extra converters.
        if (parameter is string text && text.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        var visible = value is Visibility.Visible;
        if (parameter is string text && text.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }

        return visible;
    }
}

/// <summary>Maps a boolean to one of two brushes supplied through the converter parameter.</summary>
public sealed class BooleanToBrushConverter : IValueConverter
{
    public object? TrueBrush { get; set; }

    public object? FalseBrush { get; set; }

    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        return flag ? TrueBrush : FalseBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Returns "是"/"否" for a boolean, used in the details tables.</summary>
public sealed class BoolToChineseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool b && b ? "是" : "否";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
