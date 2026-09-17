using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using HassConnect.Core;

namespace HassConnect.App.Services;

internal static class NetworkSensors
{
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim ConfigurationGate = new(1, 1);
    private static string? _host;
    private static IPAddress[] _destinations = [];
    private static long _resolveAfter;
    private static long _snapshotTime = -1;
    private static AdapterSnapshot? _snapshot;
    private static NetworkTrafficSample? _previousTraffic;
    private static NetworkTrafficRate? _rate;

    public static bool Supports(string id) => id is "ip_address" or "network_adapter" or "download_speed" or "upload_speed";

    public static async Task ConfigureTargetAsync(string host, CancellationToken ct = default)
    {
        await ConfigurationGate.WaitAsync(ct);
        try
        {
            lock (Gate)
            {
                if (string.Equals(_host, host, StringComparison.OrdinalIgnoreCase) && Environment.TickCount64 < _resolveAfter) return;
                if (!string.Equals(_host, host, StringComparison.OrdinalIgnoreCase))
                {
                    _host = host;
                    _destinations = [];
                    ClearSnapshot();
                }
            }

            IPAddress[] addresses;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                addresses = IPAddress.TryParse(host, out var address)
                    ? [address] : await Dns.GetHostAddressesAsync(host, timeout.Token);
            }
            catch (SocketException) { addresses = []; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { addresses = []; }

            lock (Gate)
            {
                // The interface lookup below is IPv4. IPv6-only hosts remain
                // unavailable instead of reporting an unrelated adapter.
                var destinations = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
                if (!_destinations.SequenceEqual(destinations)) ClearSnapshot();
                _destinations = destinations;
                _resolveAfter = Environment.TickCount64 + (destinations.Length == 0 ? 60_000 : 300_000);
            }
        }
        finally { ConfigurationGate.Release(); }
    }

    public static object? Read(string id)
    {
        if (!Supports(id)) throw new ArgumentOutOfRangeException(nameof(id));
        lock (Gate)
        {
            long now = Environment.TickCount64;
            if (_snapshotTime < 0 || now - _snapshotTime >= 1000)
            {
                _snapshot = FindAdapter();
                _snapshotTime = now;
                if (_snapshot is null || _previousTraffic is { } previous && previous.AdapterId != _snapshot.Adapter.Id)
                {
                    _previousTraffic = null;
                    _rate = null;
                }
            }
            if (_snapshot is null) return null;
            if (id == "ip_address") return _snapshot.Address.ToString();
            if (id == "network_adapter") return _snapshot.Adapter.Name;

            if (_previousTraffic is null || now - _previousTraffic.Value.TimestampMilliseconds >= 1000)
            {
                try
                {
                    var counters = _snapshot.Adapter.GetIPStatistics();
                    var sample = new NetworkTrafficSample(_snapshot.Adapter.Id, counters.BytesReceived, counters.BytesSent, now);
                    _rate = _previousTraffic is { } previous ? sample.RateSince(previous) : null;
                    _previousTraffic = sample;
                }
                catch (NetworkInformationException)
                {
                    _previousTraffic = null;
                    _rate = null;
                }
            }
            return id == "download_speed" ? _rate?.DownloadMegabitsPerSecond : _rate?.UploadMegabitsPerSecond;
        }
    }

    public static void Reset(string id)
    {
        if (!Supports(id)) return;
        lock (Gate) ClearSnapshot();
    }

    private static void ClearSnapshot()
    {
        _snapshot = null;
        _snapshotTime = -1;
        _previousTraffic = null;
        _rate = null;
    }

    private static AdapterSnapshot? FindAdapter()
    {
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var destination in _destinations)
            {
                if (GetBestInterface(BitConverter.ToUInt32(destination.GetAddressBytes()), out var index) != 0) continue;
                foreach (var adapter in adapters)
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up ||
                        !adapter.Supports(NetworkInterfaceComponent.IPv4)) continue;
                    var properties = adapter.GetIPProperties();
                    if (properties.GetIPv4Properties().Index != index) continue;

                    // UDP connect selects a local source address without sending
                    // any packet. It avoids guessing when an adapter has aliases.
                    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    socket.Connect(destination, 9);
                    if (socket.LocalEndPoint is IPEndPoint local &&
                        properties.UnicastAddresses.Any(address => address.Address.Equals(local.Address)))
                        return new(adapter, local.Address);
                }
            }
        }
        catch (NetworkInformationException) { }
        catch (SocketException) { }
        return null;
    }

    private sealed record AdapterSnapshot(NetworkInterface Adapter, IPAddress Address);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterface(uint destination, out uint index);
}
