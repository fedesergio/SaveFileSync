using System.Globalization;
using System.Windows.Data;

namespace SaveFileSync.App.ViewModels;

public static class Converters
{
    public static readonly IValueConverter NotNull = new NotNullConverter();

    private sealed class NotNullConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
