namespace OmniDock.Services;

/// <summary>按某个速率推算出来的结果。</summary>
internal readonly record struct UsageProjection(double ProjectedPercent, TimeSpan? ExhaustIn);

/// <summary>
/// 用窗口内的平均值估算消耗速率：额度窗口自带时间跨度（5h、7d 就是窗口长度），
/// 把重置时刻往前推一个窗口长度就是起点，已用量除以已经过去的时间就是平均速率。
///
/// 不必自己攒采样：程序一启动就有结果，也不用识别额度重置——
/// 重置后 used 归零、剩余时间变回一整个窗口，算出来自然就是新窗口的均值。
///
/// 测速率和做推算是分开的两步，因为长窗口自己的平均会把近期的猛涨摊平，
/// 调用方可以拿短窗口测出的速率去推算长窗口，得到更保守的结论。
/// </summary>
internal static class UsageForecast
{
    /// <summary>样本再短也得有这么长。</summary>
    private static readonly TimeSpan MinElapsed = TimeSpan.FromMinutes(2);

    /// <summary>样本至少要覆盖窗口长度的这个比例，长窗口才不会被几分钟的数据带偏。</summary>
    private const double MinElapsedFraction = 0.01d;

    /// <summary>低于这个速率就当作没在消耗。</summary>
    private const double IdleThreshold = 0.5d;

    /// <summary>
    /// 窗口内的平均速率（每分钟）。样本太短返回 null，没在消耗返回 0。
    /// </summary>
    internal static double? MeasureRate(string windowName, double used, DateTimeOffset resetAt, DateTimeOffset now)
    {
        if (ParseWindowLength(windowName) is not { } length || length <= TimeSpan.Zero)
        {
            return null;
        }

        // 窗口起点 = 重置时刻往前推一个窗口长度；已过去 = 窗口长度 - 剩余时间。
        // reset_at 过期还没刷新时会超出窗口长度，夹回来
        var elapsed = TimeSpan.FromTicks(
            Math.Clamp((length - (resetAt - now)).Ticks, 0L, length.Ticks));

        // 长窗口需要更长的样本：两分钟的数据推算 7 天纯属噪声，
        // 却足够触发一次撞墙误报
        var minElapsed = TimeSpan.FromTicks(
            Math.Max(MinElapsed.Ticks, (long)(length.Ticks * MinElapsedFraction)));

        if (elapsed < minElapsed)
        {
            return null;
        }

        var rate = used / elapsed.TotalMinutes;
        return rate <= IdleThreshold ? 0d : rate;
    }

    /// <summary>按给定速率推算：到重置时会用到哪里，以及多久会用尽。</summary>
    internal static UsageProjection Project(
        double used,
        double budget,
        double ratePerMinute,
        TimeSpan untilReset)
    {
        var minutesLeft = Math.Max(0d, untilReset.TotalMinutes);
        var projectedUsed = used + ratePerMinute * minutesLeft;
        var projectedPercent = budget > 0d ? projectedUsed / budget * 100d : 0d;

        var remaining = budget - used;
        TimeSpan? exhaustIn = ratePerMinute <= 0d
            ? null
            : remaining <= 0d
                ? TimeSpan.Zero
                : TimeSpan.FromMinutes(remaining / ratePerMinute);

        return new UsageProjection(projectedPercent, exhaustIn);
    }

    /// <summary>窗口名就是它的长度：5h、7d、30m、2w。</summary>
    internal static TimeSpan? ParseWindowLength(string name)
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
