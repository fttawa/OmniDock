namespace OmniDock.Services;

/// <summary>
/// 按最近一段时间的采样估算消耗速率，并推算按这个速度多久会用光。
/// 用量是阶梯式上涨的（只在真的消耗时才动），所以用最小二乘拟合斜率，
/// 比首尾差分稳，不会因为首尾恰好落在平台期就得出 0。
/// </summary>
internal sealed class UsageForecast
{
    /// <summary>只看最近这段时间，太久以前的速度没有参考价值。</summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(6);

    /// <summary>至少要这么多点、跨过这么长时间才敢给结论。</summary>
    private const int MinSamples = 4;
    private static readonly TimeSpan MinSpan = TimeSpan.FromSeconds(45);

    private readonly List<(DateTimeOffset At, double Used)> _samples = [];

    /// <summary>每分钟消耗量；数据不够或没在消耗时为 null。</summary>
    internal double? RatePerMinute { get; private set; }

    /// <summary>按当前速率还有多久用光；估不出来就是 null。</summary>
    internal TimeSpan? ExhaustIn { get; private set; }

    internal void Add(double used, double budget, DateTimeOffset now)
    {
        // 额度重置会让 used 掉回去，之前的历史全部作废
        if (_samples.Count > 0 && used < _samples[^1].Used - 0.001d)
        {
            _samples.Clear();
        }

        _samples.Add((now, used));
        _samples.RemoveAll(sample => now - sample.At > Window);

        Recalculate(used, budget);
    }

    /// <summary>换了额度窗口或需要重新起算时清空。</summary>
    internal void Reset()
    {
        _samples.Clear();
        RatePerMinute = null;
        ExhaustIn = null;
    }

    private void Recalculate(double used, double budget)
    {
        RatePerMinute = null;
        ExhaustIn = null;

        if (_samples.Count < MinSamples)
        {
            return;
        }

        var origin = _samples[0].At;
        var span = _samples[^1].At - origin;
        if (span < MinSpan)
        {
            return;
        }

        // 最小二乘拟合 used = a + b * 分钟数，b 就是速率
        double sumX = 0d, sumY = 0d, sumXx = 0d, sumXy = 0d;
        foreach (var (at, value) in _samples)
        {
            var x = (at - origin).TotalMinutes;
            sumX += x;
            sumY += value;
            sumXx += x * x;
            sumXy += x * value;
        }

        var n = _samples.Count;
        var denominator = n * sumXx - sumX * sumX;
        if (Math.Abs(denominator) < 1e-9d)
        {
            return;
        }

        var slope = (n * sumXy - sumX * sumY) / denominator;

        // 负斜率只可能来自抖动，当作没在消耗
        if (slope <= 0.5d)
        {
            RatePerMinute = 0d;
            return;
        }

        RatePerMinute = slope;

        var remaining = budget - used;
        ExhaustIn = remaining <= 0d
            ? TimeSpan.Zero
            : TimeSpan.FromMinutes(remaining / slope);
    }
}
