namespace OmniDock.Services;

/// <summary>窗口内的平均消耗速率，以及按这个速率还能撑多久。</summary>
internal readonly record struct UsageRate(double PerMinute, TimeSpan? ExhaustIn);

/// <summary>
/// 直接用窗口内的平均值估算消耗速率：额度窗口自带时间跨度（5h、7d 就是窗口长度），
/// 把重置时刻往前推一个窗口长度就是起点，已用量除以已经过去的时间就是平均速率。
///
/// 这样不必自己攒采样：程序一启动就有结果，也不用识别额度重置——
/// 重置后 used 归零、剩余时间变回一整个窗口，算出来自然就是新窗口的均值。
/// </summary>
internal static class UsageForecast
{
    /// <summary>窗口刚开始时样本太短，均值会被放得很夸张，先不给结论。</summary>
    private static readonly TimeSpan MinElapsed = TimeSpan.FromMinutes(2);

    /// <summary>低于这个速率就当作没在消耗。</summary>
    private const double IdleThreshold = 0.5d;

    internal static UsageRate? Estimate(
        string windowName,
        double used,
        double budget,
        DateTimeOffset resetAt,
        DateTimeOffset now)
    {
        if (ParseWindowLength(windowName) is not { } length || length <= TimeSpan.Zero)
        {
            return null;
        }

        // 窗口起点 = 重置时刻往前推一个窗口长度；已过去 = 窗口长度 - 剩余时间
        var elapsed = length - (resetAt - now);

        // reset_at 过期还没刷新时 elapsed 会超出窗口长度，夹回来
        elapsed = TimeSpan.FromTicks(Math.Clamp(elapsed.Ticks, 0L, length.Ticks));
        if (elapsed < MinElapsed)
        {
            return null;
        }

        var rate = used / elapsed.TotalMinutes;
        if (rate <= IdleThreshold)
        {
            return new UsageRate(0d, null);
        }

        var remaining = budget - used;
        var exhaustIn = remaining <= 0d
            ? TimeSpan.Zero
            : TimeSpan.FromMinutes(remaining / rate);

        return new UsageRate(rate, exhaustIn);
    }

    /// <summary>窗口名就是它的长度：5h、7d、30m、2w。</summary>
    private static TimeSpan? ParseWindowLength(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length < 2)
        {
            return null;
        }

        if (!double.TryParse(name[..^1], out var amount) || amount <= 0d)
        {
            return null;
        }

        return char.ToLowerInvariant(name[^1]) switch
        {
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            'd' => TimeSpan.FromDays(amount),
            'w' => TimeSpan.FromDays(amount * 7d),
            _ => null
        };
    }
}
