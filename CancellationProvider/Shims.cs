namespace Medallion.Threading;

internal static class Shims
{
    public static CancellationTokenSource CreateTimeoutSource(TimeSpan delay, TimeProvider timeProvider)
    {
#if NET8_0_OR_GREATER
        return new(delay, timeProvider);
#else
        return new(delay);
#endif
    }
}

#if !NET8_0_OR_GREATER
internal sealed class TimeProvider { public static TimeProvider System => new(); }
#endif
