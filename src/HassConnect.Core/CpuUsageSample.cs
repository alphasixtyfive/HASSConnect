namespace HassConnect.Core;

public readonly record struct CpuUsageSample(ulong Idle, ulong Kernel, ulong User)
{
    public double? UsageSince(CpuUsageSample previous)
    {
        if (Idle < previous.Idle || Kernel < previous.Kernel || User < previous.User)
            return null;

        // Windows includes idle time in the kernel counter.
        double total = (double)(Kernel - previous.Kernel) + (User - previous.User);
        double idle = Idle - previous.Idle;
        if (total <= 0 || idle > total) return null;
        return Math.Round((total - idle) / total * 100, 1);
    }
}
