using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DD2ModManager.Core;

namespace DD2ModManager;

/// <summary>Maps a package status to its accent colour; pass "bg" for the faint pill background.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is PackageStatus status ? status switch
        {
            PackageStatus.UpToDate => "SuccessColor",
            PackageStatus.UpdateAvailable or PackageStatus.UpdateDownloaded or PackageStatus.ProbableUpdate => "WarningColor",
            PackageStatus.NoLongerListed or PackageStatus.ArchiveMissing or PackageStatus.Unreadable => "DangerColor",
            _ => "MutedColor",
        } : "MutedColor";
        var color = (Color)Application.Current.Resources[key];
        if (parameter as string == "bg") color.A = 0x26;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Two-way binding of a RadioButton to a string property: checked when the value equals the parameter.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Equals(value?.ToString(), parameter);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? parameter : Binding.DoNothing;
}

public static class Conv
{
    public static readonly IValueConverter Not = new FuncConverter(v => v is not true);
    public static readonly IValueConverter Positive = new FuncConverter(v => v is int i && i > 0);

    private sealed class FuncConverter(Func<object?, object> convert) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => convert(value);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}

public sealed class VisibleWhenConverter : IValueConverter
{
    /// <summary>Visible for true, non-null, non-empty and non-zero values. Parameter "not" inverts.</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var truthy = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i != 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter as string == "not") truthy = !truthy;
        return truthy ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
