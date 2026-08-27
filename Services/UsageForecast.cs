using System.Globalization;

namespace OmniDock.Services;

/// <summary>按某个速率推算出来的结果。</summary>
internal readonly record struct UsageProjection(double ProjectedPercent, TimeSpan? ExhaustIn);

/// <summary>
/// 测出来的速率，外加一句「这个样本能不能拿去推算整个窗口」。
/// 速率本身随时能算，能不能外推是另一回事，两者分开报。
/// </summary>
internal readonly record struct RateSample(double PerMinute, bool Representative);

/// <summary>
/// 用窗口内的平均值估算消耗速率：额度窗口自带时间跨度（5h、7d 就是窗口长度），
/// 把重置时刻往前推一个窗口长度就是起点，已用量除以已经过去的时间就是平均速率。
///
/// 不必自己攒采样：程序一启动就有结果，也不用识别额度重置——
/// 重置后 used 归零、剩余时间变回一整个窗口，算出来自然就是新窗口的均值。
///
/// 要留神的是这个平均的分母是挂钟时间，把睡觉、吃饭、开会全算了进去。
/// 样本跨过完整的一天时这正是想要的（长期节奏本来就该含休息），可窗口刚重置
/// 那几个小时里样本几乎全是连续工作时段，算出来的是「手头正忙时的强度」，
/// 不是「这一周的节奏」。所以速率能不能外推，得看样本有没有覆盖一个昼夜。
/// </summary>
internal static class UsageForecast
{
    /// <summary>样本再短也得有这么长，否则一次大请求就能算出天文数字。</summary>
    private static readonly TimeSpan MinElapsed = TimeSpan.FromMinutes(2);

    /// <summary>窗口不短于这个长度，就算「跨天窗口」，外推要按昼夜来要求样本。</summary>
    private static readonly TimeSpan LongWindow = TimeSpan.FromDays(1);

    /// <summary>跨天窗口的外推门槛：样本得覆盖一整个昼夜，才含得上休息时间。</summary>
    private static readonly TimeSpan DayCycle = TimeSpan.FromHours(24);

    /// <summary>不跨天的窗口，样本覆盖窗口长度的这个比例就够外推了。</summary>
    private const double MinElapsedFraction = 0.01d;

    /// <summary>低于这个速率就当作没在消耗。</summary>
    private const double IdleThreshold = 0.5d;

    /// <summary>
    /// 窗口内的平均速率（每分钟）。样本太短返回 null，没在消耗返回速率 0。
    /// </summary>
    internal static RateSample? MeasureRate(string windowName, double used, DateTimeOffset resetAt, DateTimeOffset now)
    {
        if (ParseWindowLength(windowName) is not { } length || length <= TimeSpan.Zero)
        {
            return null;
        }

        // 窗口起点 = 重置时刻往前推一个窗口长度；已过去 = 窗口长度 - 剩余时间。
        // reset_at 过期还没刷新时会超出窗口长度，夹回来
        var elapsed = TimeSpan.FromTicks(
            Math.Clamp((length - (resetAt - now)).Ticks, 0L, length.Ticks));

        if (elapsed < MinElapsed)
        {
            return null;
        }

        var rate = used / elapsed.TotalMinutes;
        if (rate <= IdleThreshold)
        {
            // 「没在用」这个结论不需要长样本撑腰
            return new RateSample(0d, true);
        }

        // 跨天窗口得看满一个昼夜：只拿刚重置后的几小时去推 7 天，采到的全是
        // 连续工作时段，会算出一个根本不会发生的撞墙时间。
        // 不跨天的窗口没这个问题——它问的本来就是眼下这一阵还够不够。
        var representative = length >= LongWindow
            ? elapsed >= DayCycle
            : elapsed >= TimeSpan.FromTicks((long)(length.Ticks * MinElapsedFraction));

        return new RateSample(rate, representative);
    }

    /// <summary>
    /// 匀速用满整个窗口的速率：额度 ÷ 窗口长度。
    ///
    /// 它不依赖任何关于作息的假设，样本还不够外推时拿它当参照，
    /// 「现在 274/分、匀速线 56/分」比一个编出来的耗尽时间诚实得多。
    /// </summary>
    internal static double? EvenRate(string windowName, double budget)
    {
        if (ParseWindowLength(windowName) is not { } length || length <= TimeSpan.Zero || budget <= 0d)
        {
            return null;
        }

        return budget / length.TotalMinutes;
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

    /// <summary>
    /// 窗口名的开头就是它的长度：5h、7d、30m、2w。
    /// 后面可能跟着额度归属的后缀（7d_fable），解析时忽略——
    /// 按最后一个字符取单位会读到 e 而整个解析失败。
    /// </summary>
    internal static TimeSpan? ParseWindowLength(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length < 2)
        {
            return null;
        }

        // 先吃掉开头连续的数字，紧接着那一个字符就是单位
        var digits = 0;
        while (digits < name.Length && (char.IsAsciiDigit(name[digits]) || name[digits] == '.'))
        {
            digits++;
        }

        if (digits == 0 || digits >= name.Length)
        {
            return null;
        }

        if (!double.TryParse(name[..digits], CultureInfo.InvariantCulture, out var amount) || amount <= 0d)
        {
            return null;
        }

        return char.ToLowerInvariant(name[digits]) switch
        {
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            'd' => TimeSpan.FromDays(amount),
            'w' => TimeSpan.FromDays(amount * 7d),
            _ => null
        };
    }
}
