using System.Globalization;
using System.Windows;
using System.Windows.Data;
using AuditPlatform.Client.Core.Evidence;
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

/// <summary>Opens a reading copy of an evidence file. A file Windows would run is only shown in its folder.</summary>
internal sealed class ShellFileLauncher : IFileLauncher
{
    public void Open(string path)
    {
        var start = LocalEvidence.OpensAsDocument(path)
            ? new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }
            : new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false };
        try
        {
            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // No program is associated with this type of file.
            throw new InvalidOperationException($"Windows không mở được file này: {ex.Message}", ex);
        }
    }
}
