using AuditPlatform.Client.Core.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AuditPlatform.Client.Core.ViewModels;

/// <summary>Top-level window state: connection to the master. Engagement list and working-paper views follow.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly ISyncTransport _transport;

    public ShellViewModel(ISyncTransport transport, ClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        _transport = transport;
        MasterUrl = options.MasterUrl.ToString();
        _transport.StateChanged += (_, state) => ConnectionState = state;
        _connectionState = transport.State;
    }

    public string MasterUrl { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private ConnectionState _connectionState;

    [ObservableProperty]
    private string? _lastError;

    public string StatusText => ConnectionState switch
    {
        ConnectionState.Online => "Online — changes sync in real time",
        ConnectionState.Connecting => "Connecting…",
        _ => "Offline — changes are kept locally and sent when the connection returns",
    };

    [RelayCommand]
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        LastError = null;
        try
        {
            await _transport.ConnectAsync(cancellationToken);
        }
#pragma warning disable CA1031 // Any failure to connect leaves the client usable offline; surface it, do not crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LastError = ex.Message;
        }
    }
}
