using System.Globalization;
using System.Windows.Data;

namespace GameHours.Desktop;

public sealed class LibraryStatusTextConverter : IMultiValueConverter
{
    public object Convert(
        object[] values,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        if (values.Length < 2 ||
            values[0] is not Guid gameId ||
            values[1] is not MainWindow owner)
        {
            return string.Empty;
        }

        return owner.GetLibraryStatusText(gameId);
    }

    public object[] ConvertBack(
        object value,
        Type[] targetTypes,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
