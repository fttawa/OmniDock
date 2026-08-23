using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using OmniDock.Services;

namespace OmniDock.ViewModels;

/// <summary>界面上的一条额度窗口：占比条 + 已用/限额 + 重置倒计时 + 速率预测。</summary>
internal sealed class UsageWindowViewModel : INotifyPropertyChanged
{
    private static readonly Brush Muted = Theme.Freeze("#8AFFFFFF");

    private string _title = string.Empty;
    private string _usedText = string.Empty;
    private string _budgetText = string.Empty;
    private string _percentText = string.Empty;
    private string _resetText = string.Empty;
    private string _forecastText = string.Empty;
    private double _percent;
    private double _projectedPercent;
    private double _used;
    private double _budget;
    private double _referenceRate;
    private double _rateCap = double.PositiveInfinity;
    private double _capWindowMinutes;
    private bool _willRunOut;
    private Brush _barBrush = Theme.Accent;
    private Brush _projectionBrush = Theme.AccentSoft;
    private Brush _forecastBrush = Theme.Accent;
    private DateTimeOffset _resetAt;

    internal UsageWindowViewModel(UsageWindowDto dto) => Update(dto);

    /// <summary>服务端给的窗口标识（5h、7d），用来判断能否原地更新。</summary>
    internal string Name { get; private set; } = string.Empty;

    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    /// <summary>已用量，单独一条给翻牌器用。</summary>
    public string UsedText
    {
        get => _usedText;
        private set => Set(ref _usedText, value);
    }

    /// <summary>限额，变动极少，按普通文字排。</summary>
    public string BudgetText
    {
        get => _budgetText;
        private set => Set(ref _budgetText, value);
    }

    public string PercentText
    {
        get => _percentText;
        private set => Set(ref _percentText, value);
    }

    public string ResetText
    {
        get => _resetText;
        private set => Set(ref _resetText, value);
    }

    public double Percent
    {
        get => _percent;
        private set => Set(ref _percent, value);
    }

    public Brush BarBrush
    {
        get => _barBrush;
        private set => Set(ref _barBrush, value);
    }

    /// <summary>按当前速率，到重置时预计会用到的位置；缓冲段画到这里。</summary>
    public double ProjectedPercent
    {
        get => _projectedPercent;
        private set => Set(ref _projectedPercent, value);
    }

    public Brush ProjectionBrush
    {
        get => _projectionBrush;
        private set => Set(ref _projectionBrush, value);
    }

    /// <summary>消耗速率与耗尽预测，例如「均 26/分 · 预计用到 68%」。</summary>
    public string ForecastText
    {
        get => _forecastText;
        private set => Set(ref _forecastText, value);
    }

    public Brush ForecastBrush
    {
        get => _forecastBrush;
        private set => Set(ref _forecastBrush, value);
    }

    /// <summary>按当前速率会在重置之前用光。</summary>
    internal bool WillRunOut
    {
        get => _willRunOut;
        private set => _willRunOut = value;
    }

    /// <summary>水位过高或即将撞墙，值得提醒一下。</summary>
    internal bool NeedsAttention => WillRunOut || Percent >= 85d;

    internal void Update(UsageWindowDto dto)
    {
        Name = dto.Name;
        Title = TitleOf(dto.Name);
        _used = dto.Used;
        _budget = dto.Budget;
        WindowMinutes = UsageForecast.ParseWindowLength(dto.Name)?.TotalMinutes ?? 0d;

        var percent = dto.Budget > 0 ? dto.Used / dto.Budget * 100d : 0d;
        Percent = Math.Clamp(percent, 0d, 100d);
        // 小数位固定，否则 11% / 10.6% 之间位数一变就得整排重建，滚不起来
        PercentText = percent >= 10d ? $"{percent:0.0}%" : $"{percent:0.00}%";
        UsedText = $"{dto.Used:N0}";
        BudgetText = $"/ {dto.Budget:N0}";
        BarBrush = BrushFor(percent);

        _resetAt = DateTimeOffset.FromUnixTimeSeconds(dto.ResetAt);
        RefreshCountdown();
    }

    /// <summary>每秒调用一次，让倒计时自己走，不用重新请求。</summary>
    internal void RefreshCountdown()
    {
        RefreshForecast();

        // 低位补零，位数保持不变，数字才能一直滚而不是跳变
        var left = _resetAt - DateTimeOffset.Now;
        ResetText = left <= TimeSpan.Zero
            ? "正在重置"
            : left.TotalDays >= 1
                ? $"{(int)left.TotalDays} 天 {left.Hours:D2} 小时后重置"
                : left.TotalHours >= 1
                    ? $"{(int)left.TotalHours} 小时 {left.Minutes:D2} 分后重置"
                    : left.TotalMinutes >= 1
                        ? $"{left.Minutes} 分 {left.Seconds:D2} 秒后重置"
                        : $"{left.Seconds} 秒后重置";
    }

    /// <summary>换了强调色之后重算配色，不必等下一次请求。</summary>
    internal void RefreshAccent() => BarBrush = BrushFor(Percent);

    /// <summary>本窗口自己测出的平均速率，供外部汇总成参考速率。</summary>
    internal double? OwnRatePerMinute { get; private set; }

    /// <summary>本窗口的长度（分钟）；名字解析不出来时为 0。</summary>
    internal double WindowMinutes { get; private set; }

    /// <summary>
    /// 本窗口允许的长期平均速率上限：额度摊到整个窗口长度上。
    /// 打满这个速率就意味着每个周期刚好用尽，再快就必须等重置。
    /// </summary>
    internal double SustainableRate => WindowMinutes > 0d ? _budget / WindowMinutes : 0d;

    /// <summary>
    /// 给出推算所需的外部条件。
    /// <paramref name="referenceRate"/> 是所有窗口里最快的平均速率——长窗口自己的
    /// 平均会把近期的猛涨摊平，用它推算会偏乐观。
    /// <paramref name="cap"/> 是比本窗口更短的窗口所允许的长期平均上限：那些窗口
    /// 会先撞墙并迫使等待，所以在本窗口的尺度上跑不过这个速度。
    /// </summary>
    internal void SetRateContext(double referenceRate, double cap, double capWindowMinutes)
    {
        if (Math.Abs(_referenceRate - referenceRate) < 0.01d
            && Math.Abs(_rateCap - cap) < 0.01d
            && Math.Abs(_capWindowMinutes - capWindowMinutes) < 0.01d)
        {
            return;
        }

        _referenceRate = referenceRate;
        _rateCap = cap;
        _capWindowMinutes = capWindowMinutes;
        RefreshForecast();
    }

    /// <summary>
    /// 拿速率和重置倒计时一比：会在重置之前用光才算有风险。
    /// 每秒重算一次，因为倒计时在走，同一个速率的结论会随时间改变。
    /// </summary>
    private void RefreshForecast()
    {
        var now = DateTimeOffset.Now;
        OwnRatePerMinute = UsageForecast.MeasureRate(Name, _used, _resetAt, now);

        // 已经见底，没什么可推算的
        if (_budget > 0d && _used >= _budget)
        {
            WillRunOut = true;
            SetProjection(100d);
            ForecastText = "额度已用尽";
            ForecastBrush = Theme.Critical;
            return;
        }

        if (OwnRatePerMinute is null && _referenceRate <= 0d)
        {
            // 自己样本不够，也没有别的窗口能借速率
            WillRunOut = false;
            SetProjection(Percent);
            ForecastText = "窗口刚开始";
            ForecastBrush = Muted;
            return;
        }

        // 自己和参考速率取更快的那个：宁可早预警，也别因为长窗口把近期的
        // 猛涨摊平而漏报
        var rate = Math.Max(OwnRatePerMinute ?? 0d, _referenceRate);
        if (rate <= 0d)
        {
            WillRunOut = false;
            SetProjection(Percent);
            ForecastText = "本窗口暂无消耗";
            ForecastBrush = Muted;
            return;
        }

        var untilReset = _resetAt - now;

        // 更短的窗口会先撞墙、迫使等待，所以在本窗口余下的这段时间里，
        // 长期平均跑不过它允许的上限。只有余下时间确实跨过那个窗口的长度时才成立：
        // 如果本窗口比它先重置，就不存在被它拖住的问题。
        if (_rateCap > 0d
            && !double.IsPositiveInfinity(_rateCap)
            && untilReset.TotalMinutes > _capWindowMinutes)
        {
            rate = Math.Min(rate, _rateCap);
        }
        var projection = UsageForecast.Project(_used, _budget, rate, untilReset);
        SetProjection(projection.ProjectedPercent);

        // 显示的就是推算所用的速率，否则数字对不上会让人困惑
        var rateText = $"按 {rate:N0}/分";
        if (projection.ExhaustIn is { } exhaust && exhaust < untilReset)
        {
            WillRunOut = true;
            ForecastText = $"{rateText} · 约 {Humanize(exhaust)}后用尽";
            ForecastBrush = Theme.Critical;
            return;
        }

        WillRunOut = false;
        ForecastText = $"{rateText} · 预计用到 {ProjectedPercent:0.#}%";
        ForecastBrush = Muted;
    }

    /// <summary>缓冲段不会短于已用段，也不会超出满格。</summary>
    private void SetProjection(double projected)
    {
        ProjectedPercent = Math.Clamp(projected, Percent, 100d);
        ProjectionBrush = ProjectedPercent switch
        {
            >= 85d => Theme.CriticalSoft,
            >= 60d => Theme.WarningSoft,
            _ => Theme.AccentSoft
        };
    }

    private static string Humanize(TimeSpan span) => span switch
    {
        { TotalDays: >= 1d } => $"{(int)span.TotalDays} 天 {span.Hours} 小时",
        { TotalHours: >= 1d } => $"{(int)span.TotalHours} 小时 {span.Minutes} 分",
        { TotalMinutes: >= 1d } => $"{(int)span.TotalMinutes} 分钟",
        _ => "不到 1 分钟"
    };

    /// <summary>水位高时用固定的警示色，正常时才跟着强调色走。</summary>
    private static Brush BrushFor(double percent) => percent switch
    {
        >= 85d => Theme.Critical,
        >= 60d => Theme.Warning,
        _ => Theme.Accent
    };

    private static string TitleOf(string name) => name switch
    {
        "5h" => "5 小时",
        "7d" => "7 天",
        _ => name
    };

    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
