using Windows.Networking.Connectivity;

namespace Elevate.App.Services;

/// <summary>Reports whether the machine currently has a usable network path.</summary>
public interface INetworkMonitor
{
    bool IsOnline { get; }

    /// <summary>Raised on an arbitrary thread when <see cref="IsOnline"/> changes.</summary>
    event EventHandler? Changed;
}

/// <summary>A monitor pinned to one value; how tests keep the model offline.</summary>
public sealed class FixedNetworkMonitor(bool online) : INetworkMonitor
{
    public bool IsOnline { get; } = online;

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }
}

/// <summary>
/// The production monitor over <see cref="NetworkInformation"/>. Starts optimistic so a first
/// refresh is never suppressed while the first status read is still pending.
/// </summary>
public sealed class NetworkMonitor : INetworkMonitor, IDisposable
{
    private readonly NetworkStatusChangedEventHandler _handler;
    private volatile bool _online = true;

    public NetworkMonitor()
    {
        _handler = _ => Update();
        NetworkInformation.NetworkStatusChanged += _handler;
        Update();
    }

    public bool IsOnline => _online;

    public event EventHandler? Changed;

    public void Dispose() => NetworkInformation.NetworkStatusChanged -= _handler;

    private void Update()
    {
        bool online;
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            online = profile is not null
                && profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
        }
        catch (Exception)
        {
            // A failed read must not suppress refreshes: assume online, as the macOS monitor does at start.
            online = true;
        }

        if (online != _online)
        {
            _online = online;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
