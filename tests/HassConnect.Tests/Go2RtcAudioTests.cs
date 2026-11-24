using System.Net;
using HassConnect.HomeAssistant;

namespace HassConnect.Tests;

public sealed class Go2RtcAudioTests
{
    [Fact]
    public void SelectsSeparateReceiveAndTalkTracks()
    {
        const string description = """
            v=0
            m=audio 0 RTP/AVP 96
            a=rtpmap:96 PCMU/8000
            a=recvonly
            a=control:trackID=0
            m=audio 0 RTP/AVP 0
            a=rtpmap:0 PCMU/8000
            a=sendonly
            a=control:trackID=1
            """;

        Assert.Equal(("trackID=0", (byte)96), Go2RtcAudio.FindTrack(description, "recvonly"));
        Assert.Equal(("trackID=1", (byte)0), Go2RtcAudio.FindTrack(description, "sendonly"));
    }

    [Fact]
    public void RejectsAnAudioControlThatCanChangeTheConnectionTarget()
    {
        const string description = """
            v=0
            m=audio 0 RTP/AVP 0
            a=rtpmap:0 PCMU/8000
            a=sendonly
            a=control:rtsp://another.example/trackID=1
            """;
        Assert.Throws<InvalidDataException>(() => Go2RtcAudio.FindTrack(description, "sendonly"));
    }

    [Theory]
    [InlineData("192.168.1.21", true)]
    [InlineData("172.31.15.20", true)]
    [InlineData("172.32.15.20", false)]
    [InlineData("203.0.113.1", false)]
    [InlineData("fd12:3456::1", true)]
    public void LimitsUnencryptedAudioToLocalAddresses(string address, bool local) =>
        Assert.Equal(local, Go2RtcAudio.IsLocalAddress(IPAddress.Parse(address)));
}
