namespace HassConnect.Core;

public static class NotificationPresence
{
    public const int IdleLimitSeconds = 5 * 60;

    public static bool IsActive(string displayState, bool sessionLocked, long idleSeconds) =>
        displayState != "off" && !sessionLocked && idleSeconds is >= 0 and < IdleLimitSeconds;
}
