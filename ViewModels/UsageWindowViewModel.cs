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
    private bool _willRunOut;
    private Brush _barBrush = Theme.Accent;
    private Brush _projectionBrush = Theme.AccentSoft;
    private Brush _forecastBrush = Theme.Accent;
    private DateTimeOffset _resetAt;
    private LongTermRate? _history;
    private string? _forecastTip;

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

    /// <summary>预测依据的展开说明，鼠标停在那行字上时显示。</summary>
    public string? ForecastTip
    {
        get => _forecastTip;
        private set => Set(ref _forecastTip, value);
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

    /// <summary>
    /// 喂进从历史里读出来的长期节奏。窗口内样本还不够长时改用它推算，
    /// 这样 7 天窗口每次重置后不必再空等一天。
    /// </summary>
    internal void SetHistory(LongTermRate? rate) => _history = rate;

    /// <summary>换了强调色之后重算配色，不必等下一次请求。</summary>
    internal void RefreshAccent() => BarBrush = BrushFor(Percent);

    /// <summary>
    /// 拿本窗口自己的平均速率和重置倒计时一比：会在重置之前用光才算有风险。
    ///
    /// 只用自己的平均，不跟别的窗口互相借用或封顶：每个窗口的观测尺度正好匹配它
    /// 要回答的问题——5 小时窗口反映最近不到一小时的强度，适合判断眼下要不要减速；
    /// 7 天窗口反映几天的均值，适合判断这周还够不够。两行数字并排摆着，本身就说明
    /// 了当前强度相对本周平均是高还是低。
    ///
    /// 跨天窗口在样本凑满一个昼夜之前不下撞墙结论：那会儿采到的全是连续工作时段，
    /// 拿它外推等于假设人接下来几天不吃不睡。这种时候改成把当前强度和匀速线
    /// 并排摆着，超没超速一眼能看出来，又不必替用户编一个耗尽时间。
    ///
    /// 每秒重算一次，因为倒计时在走，同一个速率的结论会随时间改变。
    /// </summary>
    private void RefreshForecast()
    {
        var now = DateTimeOffset.Now;

        // 已经见底，没什么可推算的
        if (_budget > 0d && _used >= _budget)
        {
            WillRunOut = true;
            SetProjection(100d);
            ForecastText = "额度已用尽";
            ForecastTip = null;
            ForecastBrush = Theme.Critical;
            return;
        }

        if (UsageForecast.MeasureRate(Name, _used, _resetAt, now) is not { } sample)
        {
            // 窗口刚开始，分母太小，均值会离谱
            WillRunOut = false;
            SetProjection(Percent);
            ForecastText = "窗口刚开始";
            ForecastTip = null;
            ForecastBrush = Muted;
            return;
        }

        if (sample.PerMinute <= 0d)
        {
            WillRunOut = false;
            SetProjection(Percent);
            ForecastText = "本窗口暂无消耗";
            ForecastTip = null;
            ForecastBrush = Muted;
            return;
        }

        // 窗口内样本还没覆盖一个昼夜。历史里若已攒够跨昼夜的记录，就拿它来推
        // ——它同样含着休息时间，只是不随窗口重置清零。
        var rate = sample.PerMinute;
        var rateText = $"均 {rate:N0}/分";

        if (!sample.Representative)
        {
            if (_history is not { } history)
            {
                // 连历史也没有：报现状，不外推。缓冲段也不画——画出来
                // 就等于宣称「接下来会一直这么用」，而这正是要避免的那个假设
                WillRunOut = false;
                SetProjection(Percent);
                ForecastText = UsageForecast.EvenRate(Name, _budget) is { } even
                    ? $"现 {rate:N0}/分 · 匀速 {even:N0}/分"
                    : $"现 {rate:N0}/分";
                ForecastTip = "窗口刚开始不久，历史也还不满一天，暂不外推。"
                    + "「匀速」是把额度均分到整个窗口的速率，用来对照当前快慢";
                ForecastBrush = Muted;
                return;
            }

            rate = history.PerMinute;
            rateText = $"近 {Math.Max(1, (int)Math.Round(history.Span.TotalDays))} 天 {rate:N0}/分";

            // 把摊平前的样子也说清楚：这个平均之所以远低于当前强度，
            // 就是因为一天里大半时间根本没在用
            ForecastTip = history.ActiveFraction > 0d
                ? $"取自最近 {history.Span.TotalHours:0} 小时的记录：其中约 {history.ActiveFraction * 100d:0}% 的时间在消耗，"
                  + $"活跃时约 {rate / history.ActiveFraction:N0}/分，摊到全天就是 {rate:N0}/分"
                : $"取自最近 {history.Span.TotalHours:0} 小时的记录";
        }

        if (sample.Representative)
        {
            // 只有跨天窗口才拿「够不够一个昼夜」说事，5 小时窗口的门槛不是这个
            ForecastTip = UsageForecast.ParseWindowLength(Name) is { TotalDays: >= 1d }
                ? "按本窗口开始至今的平均消耗推算——这段已经跨过完整的昼夜，含得上休息时间"
                : "按本窗口开始至今的平均消耗推算";
        }

        var untilReset = _resetAt - now;
        var projection = UsageForecast.Project(_used, _budget, rate, untilReset);
        SetProjection(projection.ProjectedPercent);

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

    /// <summary>
    /// 名字形如 5h / 7d / 7d_fable：下划线前是窗口长度，后面是这份额度的归属。
    /// 不写死具体名字，服务端将来加别的窗口也能显示成人话。
    /// </summary>
    private static string TitleOf(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var split = name.IndexOf('_');
        var label = LengthLabel(split < 0 ? name : name[..split]);

        if (split < 0 || split + 1 >= name.Length)
        {
            return label;
        }

        var owner = name[(split + 1)..];
        return $"{label} · {char.ToUpperInvariant(owner[0])}{owner[1..]}";
    }

    private static string LengthLabel(string token)
    {
        if (UsageForecast.ParseWindowLength(token) is not { } span)
        {
            return token;
        }

        return span switch
        {
            { TotalDays: >= 1d } => $"{span.TotalDays:0.#} 天",
            { TotalHours: >= 1d } => $"{span.TotalHours:0.#} 小时",
            _ => $"{span.TotalMinutes:0.#} 分钟"
        };
    }

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
