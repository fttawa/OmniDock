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
    private double _used;
    private double _budget;
    private bool _willRunOut;
    private Brush _barBrush = Theme.Accent;
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

    /// <summary>消耗速率与耗尽预测，例如「≈ 1,240/分 · 约 8 分钟后用尽」。</summary>
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

    /// <summary>
    /// 拿窗口内的平均速率和重置倒计时一比：会在重置之前用光才算有风险。
    /// 每秒重算一次，因为倒计时在走，同一个速率的结论会随时间改变。
    /// </summary>
    private void RefreshForecast()
    {
        var now = DateTimeOffset.Now;
        if (UsageForecast.Estimate(Name, _used, _budget, _resetAt, now) is not { } estimate)
        {
            // 窗口刚开始，或者窗口名不认识
            WillRunOut = false;
            ForecastText = "窗口刚开始";
            ForecastBrush = Muted;
            return;
        }

        if (estimate.PerMinute <= 0d)
        {
            WillRunOut = false;
            ForecastText = "本窗口暂无消耗";
            ForecastBrush = Muted;
            return;
        }

        var rateText = $"均 {estimate.PerMinute:N0}/分";
        if (estimate.ExhaustIn is not { } exhaust)
        {
            WillRunOut = false;
            ForecastText = rateText;
            ForecastBrush = Muted;
            return;
        }

        if (exhaust < _resetAt - now)
        {
            WillRunOut = true;
            ForecastText = $"{rateText} · 约 {Humanize(exhaust)}后用尽";
            ForecastBrush = Theme.Critical;
            return;
        }

        WillRunOut = false;
        ForecastText = $"{rateText} · 重置前用不完";
        ForecastBrush = Muted;
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
