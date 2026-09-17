namespace HassConnect.Core;

public readonly record struct NetworkTrafficSample(string AdapterId, long ReceivedBytes, long SentBytes, long TimestampMilliseconds)
{
    public NetworkTrafficRate? RateSince(NetworkTrafficSample previous)
    {
        if (AdapterId != previous.AdapterId || string.IsNullOrEmpty(AdapterId) ||
            ReceivedBytes < 0 || SentBytes < 0 || previous.ReceivedBytes < 0 || previous.SentBytes < 0 ||
            ReceivedBytes < previous.ReceivedBytes || SentBytes < previous.SentBytes ||
            TimestampMilliseconds <= previous.TimestampMilliseconds)
            return null;

        double milliseconds = TimestampMilliseconds - previous.TimestampMilliseconds;
        return new(
            Math.Round((ReceivedBytes - previous.ReceivedBytes) * 0.008 / milliseconds, 3),
            Math.Round((SentBytes - previous.SentBytes) * 0.008 / milliseconds, 3));
    }
}

public readonly record struct NetworkTrafficRate(double DownloadMegabitsPerSecond, double UploadMegabitsPerSecond);
