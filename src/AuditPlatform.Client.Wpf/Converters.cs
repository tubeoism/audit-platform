using System.Globalization;
using System.Windows;
using System.Windows.Data;
using AuditPlatform.Client.Core.ViewModels;

namespace AuditPlatform.Client.Wpf;

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>
/// Collapses an element whose bound value is null, an empty string, false or zero. With parameter "absent" it does
/// the opposite: the element shows only when the value is missing.
/// </summary>
public sealed class PresentToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null or "" or false or 0 == parameter is "absent" ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

internal sealed class WpfDispatcher(Application application) : IUiDispatcher
{
    public void Post(Action action) => application.Dispatcher.BeginInvoke(action);
}
