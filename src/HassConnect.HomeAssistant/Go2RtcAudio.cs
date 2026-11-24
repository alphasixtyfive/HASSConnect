using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using HassConnect.Core;

namespace HassConnect.HomeAssistant;

/// <summary>G.711 µ-law listening and optional talkback on a local go2rtc RTSP connection.</summary>
public sealed class Go2RtcAudio : IDisposable
{
    private const int Port = 8554;
    private readonly TcpClient _client;
    private readonly NetworkStream _network;
    private readonly BufferedStream _input;
    private readonly byte[] _singleByte = new byte[1];
    private readonly byte[] _frameHeader = new byte[4];
    private readonly byte[] _rtp = new byte[4096];
    private readonly uint _ssrc = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
    private ushort _sequence = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue);
    private uint _timestamp;
    private int _cseq;
    private byte _receivePayload;
    private byte? _talkPayload;

    private Go2RtcAudio(TcpClient client)
    {
        _client = client;
        _network = client.GetStream();
        _input = new BufferedStream(_network, 8192);
    }

    public static async Task<Go2RtcAudio> ConnectAsync(Uri server, string entityId, bool talkback, CancellationToken ct)
    {
        if (!CameraEntityId.IsValid(entityId)) throw new ArgumentException("Choose a camera first.", nameof(entityId));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var addresses = await Dns.GetHostAddressesAsync(server.DnsSafeHost, timeout.Token).ConfigureAwait(false);
        var localAddresses = addresses.Where(IsLocalAddress).ToArray();
        if (localAddresses.Length == 0) throw new InvalidOperationException("Camera audio requires a local Home Assistant address.");
        var client = new TcpClient { NoDelay = true };
        Go2RtcAudio? audio = null;
        try
        {
            await client.ConnectAsync(localAddresses, Port, timeout.Token).ConfigureAwait(false);
            audio = new(client);
            var address = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            var origin = new UriBuilder("rtsp", address.ToString(), Port, entityId["camera.".Length..]).Uri;
            await audio.OpenAsync(origin, talkback, timeout.Token).ConfigureAwait(false);
            return audio;
        }
        catch
        {
            if (audio is null) client.Dispose();
            else audio.Dispose();
            throw;
        }
    }

    private async Task OpenAsync(Uri uri, bool talkback, CancellationToken ct)
    {
        var describe = new UriBuilder(uri) { Query = talkback ? "audio&backchannel=1" : "audio" }.Uri;
        var response = await RequestAsync("DESCRIBE", describe, "Accept: application/sdp\r\n", ct).ConfigureAwait(false);
        var receive = FindTrack(response.Body, "recvonly");
        _receivePayload = receive.Payload;
        var root = new Uri(uri.AbsoluteUri + "/");
        var setup = await RequestAsync("SETUP", new Uri(root, receive.Control),
            "Transport: RTP/AVP/TCP;unicast;interleaved=0-1\r\n", ct).ConfigureAwait(false);
        var session = setup.Headers.GetValueOrDefault("Session", "").Split(';', 2)[0].Trim();
        if (string.IsNullOrEmpty(session) || session.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidDataException("go2rtc did not establish an audio session.");
        if (talkback)
        {
            var send = FindTrack(response.Body, "sendonly");
            await RequestAsync("SETUP", new Uri(root, send.Control),
                $"Transport: RTP/AVP/TCP;unicast;interleaved=2-3\r\nSession: {session}\r\n", ct).ConfigureAwait(false);
            _talkPayload = send.Payload;
        }
        await RequestAsync("PLAY", uri, $"Session: {session}\r\n", ct).ConfigureAwait(false);
    }

    internal static (string Control, byte Payload) FindTrack(string sdp, string direction)
    {
        const string trackPrefix = "a=control:trackID=";
        const string payloadPrefix = "a=rtpmap:";
        const string codec = " PCMU/8000";
        foreach (var section in sdp.Replace("\r", "", StringComparison.Ordinal).Split("\nm="))
        {
            var lines = section.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0 || !lines[0].StartsWith("audio ", StringComparison.Ordinal) || !lines.Contains($"a={direction}")) continue;
            var control = lines.FirstOrDefault(line => line.StartsWith(trackPrefix, StringComparison.Ordinal));
            if (control is null || !byte.TryParse(control.AsSpan(trackPrefix.Length), out var track)) continue;
            foreach (var line in lines)
            {
                if (!line.StartsWith(payloadPrefix, StringComparison.Ordinal) || !line.EndsWith(codec, StringComparison.OrdinalIgnoreCase)) continue;
                if (byte.TryParse(line.AsSpan(payloadPrefix.Length, line.Length - payloadPrefix.Length - codec.Length), out var payload) && payload <= 127)
                    return ($"trackID={track}", payload);
            }
        }
        throw new InvalidDataException(direction == "sendonly"
            ? "This go2rtc stream does not offer PCMU talkback."
            : "This go2rtc stream does not offer PCMU playback.");
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadAudioAsync(CancellationToken ct)
    {
        while (true)
        {
            await _input.ReadExactlyAsync(_frameHeader, ct).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt16BigEndian(_frameHeader.AsSpan(2));
            if (_frameHeader[0] != '$' || length > _rtp.Length) throw new InvalidDataException("Invalid go2rtc audio packet.");
            await _input.ReadExactlyAsync(_rtp.AsMemory(0, length), ct).ConfigureAwait(false);
            if (_frameHeader[1] != 0 || length < 12 || (_rtp[0] >> 6) != 2 || (_rtp[1] & 127) != _receivePayload) continue;
            var offset = 12 + (_rtp[0] & 15) * 4;
            if ((_rtp[0] & 0x10) != 0)
            {
                if (offset + 4 > length) continue;
                offset += 4 + BinaryPrimitives.ReadUInt16BigEndian(_rtp.AsSpan(offset + 2)) * 4;
            }
            var padding = (_rtp[0] & 0x20) != 0 ? _rtp[length - 1] : 0;
            if (offset < length - padding) return _rtp.AsMemory(offset, length - padding - offset);
        }
    }

    public ValueTask SendAudioAsync(ReadOnlyMemory<byte> samples, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (samples.Length is < 1 or > 2048) throw new ArgumentOutOfRangeException(nameof(samples));
        var payload = _talkPayload ?? throw new InvalidOperationException("This session is listening only.");
        var packet = new byte[16 + samples.Length];
        packet[0] = (byte)'$';
        packet[1] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)(12 + samples.Length));
        packet[4] = 0x80;
        packet[5] = payload;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), _sequence++);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), _timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), _ssrc);
        samples.CopyTo(packet.AsMemory(16));
        _timestamp += (uint)samples.Length;
        return _network.WriteAsync(packet, ct);
    }

    private async Task<(Dictionary<string, string> Headers, string Body)> RequestAsync(string method, Uri uri, string headers, CancellationToken ct)
    {
        var sequence = ++_cseq;
        await _network.WriteAsync(Encoding.ASCII.GetBytes($"{method} {uri.AbsoluteUri} RTSP/1.0\r\nCSeq: {sequence}\r\n{headers}\r\n"), ct)
            .ConfigureAwait(false);
        var status = await ReadLineAsync(ct).ConfigureAwait(false);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 40; i++)
        {
            var line = await ReadLineAsync(ct).ConfigureAwait(false);
            if (line.Length == 0) break;
            var colon = line.IndexOf(':');
            if (colon <= 0) throw new InvalidDataException("Invalid go2rtc response.");
            fields[line[..colon]] = line[(colon + 1)..].Trim();
            if (i == 39) throw new InvalidDataException("Invalid go2rtc response.");
        }
        if (!status.StartsWith("RTSP/1.0 200 ", StringComparison.Ordinal) ||
            !int.TryParse(fields.GetValueOrDefault("CSeq"), out var responseSequence) || responseSequence != sequence)
            throw new InvalidDataException("go2rtc could not open this camera’s audio.");
        if (!fields.TryGetValue("Content-Length", out var contentLength)) return (fields, "");
        if (!int.TryParse(contentLength, out var length) || length is < 0 or > 16384)
            throw new InvalidDataException("Invalid go2rtc response.");
        var bytes = new byte[length];
        await _input.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return (fields, Encoding.ASCII.GetString(bytes));
    }

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var line = new StringBuilder();
        for (var length = 0; length < 4096; length++)
        {
            await _input.ReadExactlyAsync(_singleByte, ct).ConfigureAwait(false);
            if (_singleByte[0] == '\n') return line.ToString();
            if (_singleByte[0] != '\r') line.Append((char)_singleByte[0]);
        }
        throw new InvalidDataException("Invalid go2rtc response.");
    }

    internal static bool IsLocalAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.IsIPv6UniqueLocal) return true;
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
            bytes[0] == 192 && bytes[1] == 168);
    }

    public void Dispose()
    {
        _input.Dispose();
        _client.Dispose();
    }
}
